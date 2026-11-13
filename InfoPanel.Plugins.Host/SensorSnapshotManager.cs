using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Plugins.Loader;
using Serilog;

namespace InfoPanel.Plugins.Host
{
    /// <summary>
    /// Finds the plugin entries whose values changed since the host last delivered them. <see cref="GetChangedEntries"/>
    /// only reads: what it returns counts as sent once the host confirms delivery with <see cref="MarkSent"/>, so a
    /// cycle that fails part way, or whose message is never sent, costs no entry its update.
    /// </summary>
    public class SensorSnapshotManager
    {
        // Per entry key: what the host last delivered.
        private readonly ConcurrentDictionary<string, object?> _lastValues = new();
        // Per entry key: what GetChangedEntries last returned as a change, until MarkSent confirms it delivered.
        private readonly ConcurrentDictionary<string, object?> _pending = new();
        // One host loads every plugin in a DLL into one manager, and two of them may use the same container and entry
        // ids: the plugin id is part of the key, so each keeps its own state.
        private readonly ConcurrentDictionary<(string pluginId, string containerId, string entryId), string> _keyCache = new();
        // Per table key: how many cycles in a row its read has failed and it was skipped.
        private readonly ConcurrentDictionary<string, int> _skipStreaks = new();
        // The text and table keys whose cut has been reported (once each).
        private readonly ConcurrentDictionary<string, bool> _cutReported = new();
        private readonly ILogger _logger;
        private readonly Action<string, string>? _reportCut;

        /// <summary>
        /// Consecutive skipped reads of one table after which it is reported, once per streak: about 5 s of the host's
        /// 100 ms cycles.
        /// </summary>
        internal const int SkipWarningCycles = 50;

        /// <param name="logger">The log; the process's by default.</param>
        /// <param name="reportCut">
        /// Told (plugin id, message) once per text or table that is cut to what the engine takes (<see cref="IpcLimits"/>).
        /// </param>
        /// <param name="time">The clock the picture rates are kept by; the system's by default.</param>
        public SensorSnapshotManager(ILogger? logger = null, Action<string, string>? reportCut = null, TimeProvider? time = null)
        {
            _logger = logger ?? Log.ForContext<SensorSnapshotManager>();
            _reportCut = reportCut;
            _time = time ?? TimeProvider.System;
            (_imageWire, _imageCount) = (WireBucket(), CountBucket());
            Wire = new WireBudget(_time);
        }

        /// <summary>
        /// The host's budget for everything it sends (<see cref="IpcLimits"/>): a picture is reserved against it as against
        /// the pictures' own rates; the host's messages of readings and its reports take from it too.
        /// </summary>
        public WireBudget Wire { get; }

        // Per entry key: the order its change began to wait in, from when the host first took it to send until it was sent
        // (MarkSent): a change the wire held back goes before those that began to wait after it.
        private readonly ConcurrentDictionary<string, long> _waiting = new();
        private long _waitOrder;

        /// <summary>
        /// The order <paramref name="update"/>'s change (of <paramref name="pluginId"/>'s entry) began to wait in: given the
        /// first time the host takes it to send, and kept, whatever newer value it carries, until it is sent.
        /// </summary>
        public long WaitingOrder(string pluginId, EntryUpdateDto update) =>
            _waiting.GetOrAdd(Key(pluginId, update.ContainerId, update.EntryId), _ => Interlocked.Increment(ref _waitOrder));

        private RateBucket WireBucket() => new(IpcLimits.ImageWireBurstBytes, IpcLimits.ImageWireBytesPerSecond, _time);

        private RateBucket CountBucket() => new(IpcLimits.ImagesBurst, IpcLimits.ImagesPerSecond, _time);

        private static readonly TimeSpan ImageInterval = TimeSpan.FromMilliseconds(IpcLimits.ImageIntervalMs);

        private readonly TimeProvider _time;
        // Per image entry key, under its own lock (with the rates below): what was read of it and when it may go.
        private readonly Dictionary<string, ImageTrack> _images = [];
        // The host's picture rates (IpcLimits): wire bytes and pictures, each with its burst.
        private RateBucket _imageWire;
        private RateBucket _imageCount;

        /// <summary>Tests only: how many times a picture has been hashed.</summary>
        internal int ImageHashes { get; private set; }

        internal sealed class ImageTrack
        {
            // The picture read last, as the plugin holds it (never changed once given), and its SHA-256, taken once it may go.
            public ReadOnlyMemory<byte> Seen;
            public string? Hash;
            // When its picture was last hashed: at most once an ImageInterval, whatever the plugin sets meanwhile.
            public long? HashedAt;
            // When a picture not yet delivered began to wait (a newer one keeps its place), and when the entry's last
            // picture's send ended (its next may go an ImageInterval after).
            public long? WaitingSince;
            public long? SentAt;
        }

        /// <summary>
        /// One image entry's change found by <see cref="GetChangedImages"/>: no picture (<see cref="IsPicture"/> false),
        /// which goes with the readings, or a picture, which goes in a message of its own once <see cref="TryReserve"/>
        /// has reserved it, and is released (<see cref="Release"/>) when its send ends.
        /// </summary>
        public sealed class ImageSend
        {
            internal ImageSend(string pluginId, string key, EntryUpdateDto update, ImageTrack? track)
            {
                (PluginId, Key, Update, Track) = (pluginId, key, update, track);
            }

            public string PluginId { get; }
            public EntryUpdateDto Update { get; }
            public bool IsPicture => Track != null;
            internal string Key { get; }
            internal ImageTrack? Track { get; }
            internal long WireBytes { get; set; }
            // A picture still waiting for its hash: it keeps its place in the order, and can't be reserved yet.
            internal bool Unhashed { get; init; }
        }

        /// <summary>
        /// The image entries' changes this cycle, in the order to send them: each no picture where one was delivered (a
        /// picture over <see cref="PluginImage.MaxBytes"/> is left out, as no picture, with one warning), then each picture
        /// new by its SHA-256 that may go: its entry's last send ended <see cref="IpcLimits.ImageIntervalMs"/> ago. Each
        /// entry is read once; a picture is hashed at most once a second, the newest one set waiting meanwhile, in its place
        /// in the order but not to be reserved (so the cycle's pictures stop at it). Pictures come in the order their
        /// entries began to wait, a waiting entry's newer picture in place of the one before, which never goes. Nothing is
        /// reserved here: each picture is reserved alone, just before its send (<see cref="TryReserve"/>), and the cycle
        /// sends no more once one can't be, so a picture that waits longer keeps the wire for itself. As with
        /// <see cref="GetChangedEntries"/>, each counts as delivered once <see cref="MarkSent"/> confirms it.
        /// </summary>
        public List<ImageSend> GetChangedImages(IEnumerable<PluginWrapper> wrappers)
        {
            var clears = new List<ImageSend>();
            var due = new List<(long Since, int Order, ImageSend Send)>();
            lock (_images)
            {
                var now = _time.GetTimestamp();
                foreach (var wrapper in wrappers)
                {
                    foreach (var container in wrapper.PluginContainers)
                    {
                        foreach (var entry in container.Entries)
                        {
                            if (entry is not IPluginImage image)
                                continue;
                            var key = Key(wrapper.Id, container.Id, entry.Id);
                            if (!_images.TryGetValue(key, out var track))
                                _images[key] = track = new ImageTrack();

                            var bytes = image.Value;
                            if (bytes.Length > PluginImage.MaxBytes)
                            {
                                ReportCut(wrapper.Id, key, $"Image '{entry.Id}' is over {PluginImage.MaxBytes / (1024 * 1024)} MiB; it is left out, as no picture");
                                bytes = default;
                            }
                            // What was delivered: a picture's SHA-256, or null for none (the catalogue lists none).
                            var delivered = _lastValues.TryGetValue(key, out var last) ? last as string : null;
                            var update = new EntryUpdateDto { ContainerId = container.Id, EntryId = entry.Id, Type = "image" };

                            if (bytes.IsEmpty)
                            {
                                (track.Seen, track.Hash, track.WaitingSince) = (default, null, null);
                                if (delivered != null)
                                {
                                    _pending[key] = null;
                                    clears.Add(new ImageSend(wrapper.Id, key, update, null));
                                }
                                continue;
                            }

                            if (!bytes.Equals(track.Seen))
                                (track.Seen, track.Hash) = (bytes, null);
                            if (track.Hash != null && track.Hash == delivered)
                            {
                                track.WaitingSince = null;
                                continue;
                            }
                            track.WaitingSince ??= now;
                            if (track.SentAt is { } sent && _time.GetElapsedTime(sent, now) < ImageInterval)
                                continue;
                            if (track.Hash == null)
                            {
                                // Hashed only once it may go, and at most once a second: a plugin that sets the same picture
                                // again and again (a new array each time) costs one hash a second. Meanwhile it keeps its place
                                // in the order, unreserved, so pictures that began to wait after it wait behind it: they
                                // can't take the wire it waits for (a picture found unchanged at its hash lets them go).
                                if (track.HashedAt is { } hashed && _time.GetElapsedTime(hashed, now) < ImageInterval)
                                {
                                    due.Add((track.WaitingSince.Value, due.Count, new ImageSend(wrapper.Id, key, update, track) { Unhashed = true }));
                                    continue;
                                }
                                track.Hash = Convert.ToHexString(SHA256.HashData(bytes.Span));
                                track.HashedAt = now;
                                ImageHashes++;
                            }
                            if (track.Hash == delivered)
                            {
                                track.WaitingSince = null;
                                continue;
                            }
                            due.Add((track.WaitingSince.Value, due.Count, new ImageSend(wrapper.Id, key, update, track)));
                        }
                    }
                }
            }

            clears.AddRange(due.OrderBy(d => d.Since).ThenBy(d => d.Order).Select(d => d.Send));
            return clears;
        }

        /// <summary>
        /// Reserves a picture of <see cref="GetChangedImages"/> to send now and gives its update the picture (base64): only
        /// when it has been hashed, its entry's last send ended a second ago and the host's picture rates and its wire have
        /// room for it now (its wire bytes and one picture, <see cref="IpcLimits"/>); else false, and it waits (the cycle
        /// sends no more pictures). No picture needs no reservation. A reserved picture counts against the rates whether or
        /// not its send succeeds.
        /// </summary>
        public bool TryReserve(ImageSend send)
        {
            if (send.Track is not { } track)
                return true;
            if (send.Unhashed)
                return false;
            ReadOnlyMemory<byte> bytes;
            lock (_images)
            {
                var now = _time.GetTimestamp();
                if (track.SentAt is { } sent && _time.GetElapsedTime(sent, now) < ImageInterval)
                    return false;
                bytes = track.Seen;
                // Its wire bytes: the base64 and the message around it, by the estimate the readings' messages are sized by
                // when its ids make that more than the allowance.
                var envelope = JsonSize.MessageFixed + JsonSize.BatchFixed + JsonSize.Of(send.PluginId) + JsonSize.Of(send.Update) + 2;
                send.WireBytes = IpcLimits.ImageWireBytes(bytes.Length) + Math.Max(0, envelope - IpcLimits.ImageEnvelopeBytes);
                // The pictures' own rates, then the host's wire (taken only when the rates have room).
                if (!_imageWire.CanTake(send.WireBytes) || !_imageCount.CanTake(1) || !Wire.TryTake(send.WireBytes))
                    return false;
                _imageWire.Take(send.WireBytes);
                _imageCount.Take(1);
                track.WaitingSince = null;
                _pending[send.Key] = track.Hash;
            }
            send.Update.ImageValue = PictureText.Encode(bytes.Span);
            return true;
        }

        /// <summary>
        /// A reserved picture's send ended (sent or not): its reservation was held through the send, so the host's rates
        /// fill again, and its entry's second runs, only from now.
        /// </summary>
        public void Release(ImageSend send)
        {
            if (send.Track is not { } track)
                return;
            lock (_images)
            {
                track.SentAt = _time.GetTimestamp();
                _imageWire.Hold();
                _imageCount.Hold();
                Wire.Hold();
            }
        }

        public List<EntryUpdateDto> GetChangedEntries(PluginWrapper wrapper)
        {
            var updates = new List<EntryUpdateDto>();

            foreach (var container in wrapper.PluginContainers)
            {
                foreach (var entry in container.Entries)
                {
                    var key = Key(wrapper.Id, container.Id, entry.Id);
                    EntryUpdateDto? update;
                    try
                    {
                        update = CreateUpdateIfChanged(wrapper.Id, key, container.Id, entry);
                    }
                    catch (Exception ex) when (entry is IPluginTable && IsTableChangedDuringRead(ex))
                    {
                        // The plugin modified a table while it was being read. Skip only this entry:
                        // nothing is recorded for it, so it is re-checked next cycle.
                        // Other entry types still surface their errors to the update loop.
                        ReportSkippedTable(key, ex);
                        continue;
                    }

                    if (entry is IPluginTable)
                    {
                        _skipStreaks.TryRemove(key, out _);
                    }

                    if (update != null)
                    {
                        updates.Add(update);
                    }
                }
            }

            return updates;
        }

        /// <summary>
        /// Records <paramref name="updates"/>, returned by <see cref="GetChangedEntries"/> for the plugin
        /// <paramref name="pluginId"/>, as delivered: each is compared with later reads and is not sent again until it
        /// changes. Call it only once the host has sent them.
        /// </summary>
        public void MarkSent(string pluginId, IEnumerable<EntryUpdateDto> updates)
        {
            foreach (var update in updates)
            {
                var key = Key(pluginId, update.ContainerId, update.EntryId);
                _waiting.TryRemove(key, out _);
                if (_pending.TryRemove(key, out var value))
                {
                    _lastValues[key] = value;
                }
            }
        }

        // A table changed during a read fails once and reads cleanly next cycle; one whose read keeps failing (a cell
        // getter that always throws, say) would otherwise stay stale with no trace. Report it once per streak.
        private void ReportSkippedTable(string key, Exception ex)
        {
            // Capped just past the threshold, so a table that never recovers is reported once.
            var streak = _skipStreaks.AddOrUpdate(key, 1, (_, n) => Math.Min(n + 1, SkipWarningCycles + 1));
            if (streak == SkipWarningCycles)
            {
                _logger.Warning(ex, "Plugin table {TableKey} failed to read for {Cycles} cycles in a row; it is skipped until a read succeeds",
                    key, streak);
            }
        }

        private string Key(string pluginId, string containerId, string entryId) =>
            _keyCache.GetOrAdd((pluginId, containerId, entryId), k => $"{k.pluginId}/{k.containerId}/{k.entryId}");

        // A DataTable is not thread-safe: a plugin that changes rows or columns while the host reads them makes the
        // read throw. "Collection was modified" (InvalidOperationException), a removed row (RowNotInTableException, a
        // DataException), a column removed between finding it and reading its storage (NullReferenceException) or an
        // index past a collection that shrank (IndexOutOfRangeException, ArgumentOutOfRangeException).
        private static bool IsTableChangedDuringRead(Exception ex) =>
            ex is InvalidOperationException or DataException or NullReferenceException
                or IndexOutOfRangeException or ArgumentOutOfRangeException;

        // Each entry is read once per cycle. The change check compares what was read with what was last delivered,
        // and the payload is built from the same read, so the check always describes the payload sent. Texts, units and
        // tables are cut to what the engine takes first (IpcLimits), as it would cut them, so no update can make a
        // message the engine refuses, and what is compared is what is sent.
        private EntryUpdateDto? CreateUpdateIfChanged(string pluginId, string key, string containerId, IPluginData entry)
        {
            object state;
            EntryUpdateDto update;

            if (entry is IPluginSensor sensor)
            {
                var read = PluginSensorReading.Of(sensor);
                var reading = new SensorReading(read.Value, read.ValueMin, read.ValueMax, read.ValueAvg,
                    TableDtoConverter.Cut(sensor.Unit, IpcLimits.NameChars));
                state = reading;
                update = new EntryUpdateDto
                {
                    ContainerId = containerId,
                    EntryId = entry.Id,
                    Type = "sensor",
                    SensorValue = new SensorValueDto
                    {
                        Value = reading.Value,
                        ValueMin = reading.ValueMin,
                        ValueMax = reading.ValueMax,
                        ValueAvg = reading.ValueAvg,
                        Unit = reading.Unit
                    }
                };
            }
            else if (entry is IPluginText text)
            {
                var whole = text.Value;
                var value = TableDtoConverter.Cut(whole, IpcLimits.TextChars);
                if (whole?.Length > IpcLimits.TextChars)
                    ReportCut(pluginId, key, $"Text '{entry.Id}' is over {IpcLimits.TextChars} characters; the rest is cut");
                state = value;
                update = new EntryUpdateDto
                {
                    ContainerId = containerId,
                    EntryId = entry.Id,
                    Type = "text",
                    TextValue = value
                };
            }
            else if (entry is IPluginTable table)
            {
                // The payload is the table's only read; it is compared whole, never through a hash of a second read. It is
                // cut to what the engine takes and what fits a message, so no table can make a message the engine refuses.
                var (tableValue, _, cut) = TableDtoConverter.Convert(table, IpcLimits.HostTableBytes);
                if (cut)
                    ReportCut(pluginId, key, $"Table '{entry.Id}' is over {IpcLimits.TableRows} rows, {IpcLimits.TableColumns} columns, " +
                        $"{IpcLimits.CellChars} characters a cell or {IpcLimits.HostTableBytes / (1024 * 1024)} MiB; the rest is cut");
                state = tableValue;
                update = new EntryUpdateDto
                {
                    ContainerId = containerId,
                    EntryId = entry.Id,
                    Type = "table",
                    TableValue = tableValue
                };
            }
            else
            {
                return null;
            }

            if (_lastValues.TryGetValue(key, out var last) && SameState(last, state))
                return null;

            _pending[key] = state;
            return update;
        }

        // A cut is logged and reported once per entry: an entry stays over the limit every cycle it changes.
        private void ReportCut(string pluginId, string key, string message)
        {
            if (!_cutReported.TryAdd(key, true)) return;
            _logger.Warning("Plugin entry {EntryKey}: {Message}", key, message);
            _reportCut?.Invoke(pluginId, message);
        }

        // Everything a sensor's payload carries, the unit included: a change of unit alone is an update.
        private readonly record struct SensorReading(float Value, float ValueMin, float ValueMax, float ValueAvg, string? Unit);

        private static bool SameState(object? last, object state) => (last, state) switch
        {
            (SensorReading a, SensorReading b) => a == b,
            (string a, string b) => a == b,
            (TableValueDto a, TableValueDto b) => SameTable(a, b),
            _ => false
        };

        private static bool SameTable(TableValueDto a, TableValueDto b)
        {
            if (a.DefaultFormat != b.DefaultFormat
                || !a.Columns.SequenceEqual(b.Columns)
                || a.Rows.Count != b.Rows.Count)
                return false;

            for (var r = 0; r < a.Rows.Count; r++)
            {
                var rowA = a.Rows[r];
                var rowB = b.Rows[r];
                if (rowA.Count != rowB.Count) return false;

                for (var c = 0; c < rowA.Count; c++)
                {
                    if (!SameCell(rowA[c], rowB[c])) return false;
                }
            }

            return true;
        }

        private static bool SameCell(TableCellDto a, TableCellDto b) =>
            a.Type == b.Type
            && a.TextValue == b.TextValue
            && a.SensorName == b.SensorName
            && a.SensorUnit == b.SensorUnit
            && SameSensorValue(a.SensorValue, b.SensorValue);

        private static bool SameSensorValue(SensorValueDto? a, SensorValueDto? b) =>
            a == null || b == null
                ? a == b
                : a.Value.Equals(b.Value)
                  && a.ValueMin.Equals(b.ValueMin)
                  && a.ValueMax.Equals(b.ValueMax)
                  && a.ValueAvg.Equals(b.ValueAvg)
                  && a.Unit == b.Unit;

        public void Clear()
        {
            _lastValues.Clear();
            _pending.Clear();
            _keyCache.Clear();
            _skipStreaks.Clear();
            _cutReported.Clear();
            _waiting.Clear();
            lock (_images)
            {
                _images.Clear();
                (_imageWire, _imageCount) = (WireBucket(), CountBucket());
            }
        }
    }
}
