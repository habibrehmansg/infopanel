using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using InfoPanel.Plugins.Graphics;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Plugins.Loader;
using Serilog;
using StreamJsonRpc;

namespace InfoPanel.Plugins.Host
{
    public class HostService : IPluginHostService
    {
        private readonly ILogger Logger;
        private readonly string _pluginPath;
        // Where the plugins' settings are kept ({plugin id}.json), the folder the engine gives; null keeps them in memory.
        private readonly string? _settingsDirectory;
        private JsonRpc? _jsonRpc;
        private readonly List<PluginWrapper> _wrappers = [];
        private readonly SensorSnapshotManager _snapshotManager;
        private readonly List<PluginImageWriter> _imageWriters = [];
        private readonly Dictionary<PluginImageWriter, (string PluginId, string ImageId)> _writerMappings = [];
        private CancellationTokenSource? _updateCts;
        private Task? _updateTask;
        private Task? _perfUpdateTask;

        // CPU metrics
        private Process? _currentProcess;
        private TimeSpan _lastCpuTime;
        private long _lastCheckStamp;
        private readonly TimeProvider _time;

        /// <summary>
        /// Called after plugin shutdown completes to signal the host process to exit gracefully.
        /// </summary>
        public Action? OnShutdownRequested { get; set; }

        /// <param name="pluginPath">The plugin DLL.</param>
        /// <param name="settingsDirectory">
        /// The folder for the plugins' settings, one <c>{plugin id}.json</c> each (the engine gives each package its own);
        /// null keeps settings in memory only.
        /// </param>
        /// <param name="logger">The log; the process's by default.</param>
        public HostService(string pluginPath, string? settingsDirectory = null, ILogger? logger = null)
            : this(pluginPath, settingsDirectory, logger, TimeProvider.System)
        {
        }

        /// <param name="time">The clock the host's rates are kept by (tests give their own).</param>
        internal HostService(string pluginPath, string? settingsDirectory, ILogger? logger, TimeProvider time)
        {
            _pluginPath = pluginPath;
            _settingsDirectory = settingsDirectory;
            Logger = logger ?? Log.ForContext<HostService>();
            _time = time;
            _snapshotManager = new SensorSnapshotManager(logger, NotifyError, time);
        }

        public void SetJsonRpc(JsonRpc jsonRpc)
        {
            _jsonRpc = jsonRpc;
        }

        public async Task<List<PluginMetadataDto>> InitializeAsync()
        {
            var metadataList = await LoadPluginsAsync();
            StartLoops();
            return metadataList;
        }

        /// <summary>
        /// Loads and starts the DLL's plugins and sends the catalogue, as <see cref="InitializeAsync"/> does before it starts
        /// the loops; a test entry point, so a test can run the update cycle itself.
        /// </summary>
        internal async Task<List<PluginMetadataDto>> LoadPluginsAsync()
        {
            Logger.Information("Initializing plugins from {PluginPath}", _pluginPath);

            var loaded = PluginLoader.Load(_pluginPath);
            foreach (var skipped in loaded.Skipped)
            {
                Logger.Information("Skipped plugin type {Type}: it runs only on {Platforms}", skipped.TypeName, string.Join(", ", skipped.Platforms));
            }
            var plugins = loaded.Plugins;
            var pluginInfo = PluginLoader.GetPluginInfo(Path.GetDirectoryName(_pluginPath)!);
            var descriptor = new PluginDescriptor(_pluginPath, pluginInfo);
            var metadataList = new List<PluginMetadataDto>();
            var containersByPluginId = new Dictionary<string, List<ContainerDto>>();
            // The catalogue is one message: what is left of the host's message budget (by JsonSize's estimate) once each
            // plugin's own fields, its containers and the entries listed so far are counted. An entry keeps its value (a
            // text's, a table's rows) only while it fits what is left, and is listed without it past that: the first
            // update sends every value, since nothing is marked sent then.
            var catalogueBytes = IpcLimits.HostMessageBytes - JsonSize.MessageFixed;

            foreach (var plugin in plugins)
            {
                var wrapper = new PluginWrapper(descriptor, plugin);
                // The image buffers made for this plugin, released if its start fails.
                var writersMade = new List<PluginImageWriter>();

                try
                {
                    await wrapper.Initialize();
                    descriptor.PluginWrappers.TryAdd(wrapper.Id, wrapper);
                    _wrappers.Add(wrapper);
                    Logger.Information("Plugin {PluginName} initialized successfully", wrapper.Name);

                    // Load and apply stored config for configurable plugins (only if plugin doesn't manage its own config file)
                    if (plugin is IPluginConfigurable earlyConfigurable && wrapper.ConfigFilePath == null)
                    {
                        LoadAndApplyConfig(wrapper, earlyConfigurable);
                        // Ensure the sidecar JSON exists (creates with defaults if missing)
                        if (GetConfigFilePath(wrapper) is { } sidecar && !File.Exists(sidecar))
                        {
                            SaveConfig(wrapper, earlyConfigurable);
                        }
                    }

                    // Discover actions
                    var actions = new List<PluginActionDto>();
                    var methods = plugin.GetType().GetMethods()
                        .Where(m => m.GetCustomAttributes(typeof(PluginActionAttribute), false).Length > 0);
                    foreach (var method in methods)
                    {
                        var attribute = (PluginActionAttribute)method.GetCustomAttributes(typeof(PluginActionAttribute), false).First();
                        actions.Add(new PluginActionDto
                        {
                            MethodName = method.Name,
                            DisplayName = attribute.DisplayName
                        });
                    }

                    // The settings as the engine lists them. A getter that throws can hold a setting's value in its text
                    // (one restored a moment ago included), so this read has a catch of its own: the start fails with the
                    // exception's kind logged and reported, never its text, and the exception goes no further.
                    List<PluginConfigPropertyDto> settings = [];
                    if (plugin is IPluginConfigurable listed)
                    {
                        try
                        {
                            settings = listed.ConfigProperties.Select(ConvertConfigPropertyToDto).ToList();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("Failed to initialize plugin {PluginName}: reading its settings failed ({Error})", wrapper.Name, ex.GetType().Name);
                            NotifyError(wrapper.Id, $"Reading its settings failed ({ex.GetType().Name})");
                            await DropAsync(descriptor, wrapper, writersMade);
                            continue;
                        }
                    }

                    var metadata = new PluginMetadataDto
                    {
                        Id = wrapper.Id,
                        // Cut as the engine cuts them.
                        Name = TableDtoConverter.Cut(wrapper.Name, IpcLimits.NameChars),
                        Description = TableDtoConverter.Cut(wrapper.Description, IpcLimits.TextChars),
                        ConfigFilePath = plugin is IPluginConfigurable && wrapper.ConfigFilePath == null
                            ? GetConfigFilePath(wrapper)
                            : wrapper.ConfigFilePath,
                        UpdateIntervalMs = wrapper.UpdateInterval.TotalMilliseconds,
                        Actions = actions,
                        IsConfigurable = plugin is IPluginConfigurable,
                        ConfigProperties = settings
                    };
                    // Detect image provider and set up shared memory buffers
                    if (plugin is IPluginImageProvider imageProvider)
                    {
                        var writers = new Dictionary<string, IPluginImageWriter>();

                        foreach (var imgDescriptor in imageProvider.ImageDescriptors)
                        {
                            var mmfName = $"InfoPanel.Image.{Environment.ProcessId}.{wrapper.Id}.{imgDescriptor.Id}";
                            var mmfSize = PluginImageWriter.ComputeMmfSize(imgDescriptor.Width, imgDescriptor.Height);

                            // ComputeMmfSize has refused a size the writer can't hold, before anything is mapped. If
                            // the view or the writer still fails, what was made here is released before the failure
                            // goes on: the writer owns the mapping only once it is made.
                            var mmf = MemoryMappedFile.CreateOrOpen(mmfName, mmfSize);
                            MemoryMappedViewAccessor? accessor = null;
                            PluginImageWriter writer;
                            try
                            {
                                accessor = mmf.CreateViewAccessor(0, mmfSize);
                                writer = new PluginImageWriter(mmfName, mmf, accessor, imgDescriptor.Width, imgDescriptor.Height);
                            }
                            catch
                            {
                                accessor?.Dispose();
                                mmf.Dispose();
                                throw;
                            }
                            writersMade.Add(writer);
                            _imageWriters.Add(writer);
                            _writerMappings[writer] = (wrapper.Id, imgDescriptor.Id);
                            writer.Resized += OnWriterResized;

                            writers[imgDescriptor.Id] = writer;

                            metadata.ImageDescriptors.Add(new ImageDescriptorDto
                            {
                                Id = imgDescriptor.Id,
                                Name = imgDescriptor.Name,
                                Width = imgDescriptor.Width,
                                Height = imgDescriptor.Height,
                                MmfName = mmfName,
                                BufferSize = mmfSize
                            });

                            Logger.Information("Created MMF {MmfName} for image {ImageId} ({W}x{H})",
                                mmfName, imgDescriptor.Id, imgDescriptor.Width, imgDescriptor.Height);
                        }

                        imageProvider.OnImageBuffersReady(writers);
                    }

                    // The plugin's own fields, and its key in the containers' map, before its entries.
                    catalogueBytes -= JsonSize.Of(metadata) + JsonSize.Of(wrapper.Id) + 4;

                    // Convert containers to DTOs
                    var containerDtos = new List<ContainerDto>();
                    foreach (var container in wrapper.PluginContainers)
                    {
                        var containerDto = new ContainerDto
                        {
                            Id = container.Id,
                            Name = TableDtoConverter.Cut(container.Name, IpcLimits.NameChars),
                            IsEphemeralPath = container.IsEphemeralPath,
                        };
                        catalogueBytes -= JsonSize.ContainerFixed + JsonSize.Of(containerDto.Id) + JsonSize.Of(containerDto.Name);
                        containerDto.Entries = container.Entries.Select(entry => ConvertEntryToDto(entry, ref catalogueBytes)).ToList();
                        containerDtos.Add(containerDto);
                    }
                    // Add image descriptors as text entries so they appear as selectable sensors. They are counted like
                    // the others, but always keep their values: no update sends them.
                    if (metadata.ImageDescriptors.Count > 0)
                    {
                        var imagesContainer = new ContainerDto
                        {
                            Id = $"__images_{wrapper.Id}",
                            Name = "Images",
                            IsEphemeralPath = false,
                            Entries = metadata.ImageDescriptors.Select(img => new EntryDto
                            {
                                Id = img.Id,
                                Name = TableDtoConverter.Cut(img.Name, IpcLimits.NameChars),
                                Type = "text",
                                TextValue = $"plugin-image://{wrapper.Id}/{img.Id}"
                            }).ToList()
                        };
                        catalogueBytes -= JsonSize.ContainerFixed + JsonSize.Of(imagesContainer.Id) + JsonSize.Of(imagesContainer.Name)
                            + imagesContainer.Entries.Sum(JsonSize.Of);
                        containerDtos.Add(imagesContainer);
                    }

                    // Listed only once everything about it converted: a plugin whose start fails isn't listed.
                    metadataList.Add(metadata);
                    containersByPluginId[wrapper.Id] = containerDtos;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to initialize plugin {PluginName}", wrapper.Name);
                    NotifyError(wrapper.Id, ex.Message);
                    await DropAsync(descriptor, wrapper, writersMade);
                }
            }

            // Initialize CPU baseline
            try
            {
                _currentProcess = Process.GetCurrentProcess();
                StartCpuBaseline(_currentProcess.TotalProcessorTime);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to initialize CPU baseline");
            }

            // Notify main app about loaded plugins: the first message, counted on the host's wire by its estimate.
            _snapshotManager.Wire.Take(IpcLimits.HostMessageBytes - catalogueBytes);
            NotifyPluginsLoaded(metadataList, containersByPluginId);

            return metadataList;
        }

        /// <summary>
        /// Takes a plugin whose start failed out of the host: no longer updated, sent or served, then stopped (its own
        /// update task ended and the plugin closed) and only then its image buffers released, so nothing of it can be
        /// drawing into a buffer as its mapping goes. A plugin whose Initialize itself threw was never added; this is safe
        /// for it.
        /// </summary>
        internal async Task DropAsync(PluginDescriptor descriptor, PluginWrapper wrapper, List<PluginImageWriter> writers)
        {
            _wrappers.Remove(wrapper);
            if (descriptor.PluginWrappers.TryGetValue(wrapper.Id, out var listed) && ReferenceEquals(listed, wrapper))
                descriptor.PluginWrappers.Remove(wrapper.Id);

            try
            {
                await wrapper.StopAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error stopping plugin {PluginName} after its start failed", wrapper.Name);
            }

            foreach (var writer in writers)
            {
                writer.Resized -= OnWriterResized;
                _imageWriters.Remove(writer);
                _writerMappings.Remove(writer);
                try { writer.Dispose(); } catch (Exception ex) { Logger.Warning(ex, "Failed to release an image buffer of plugin {PluginName}", wrapper.Name); }
            }
        }

        /// <summary>
        /// Test entry point: adds a plugin that is already initialised, as <see cref="InitializeAsync"/> does once it
        /// has loaded it, without loading a DLL.
        /// </summary>
        internal void AddInitializedWrapper(PluginWrapper wrapper) => _wrappers.Add(wrapper);

        /// <summary>Starts the update and performance loops, as <see cref="InitializeAsync"/> does once its plugins are loaded.</summary>
        internal void StartLoops()
        {
            _updateCts = new CancellationTokenSource();
            _updateTask = Task.Run(() => UpdateLoopAsync(_updateCts.Token));
            _perfUpdateTask = Task.Run(() => PerformanceLoopAsync(_updateCts.Token));
        }

        public Task<List<PluginConfigPropertyDto>> GetConfigPropertiesAsync(string pluginId)
        {
            var wrapper = _wrappers.FirstOrDefault(w => w.Id == pluginId);
            if (wrapper?.Plugin is IPluginConfigurable configurable)
            {
                return Task.FromResult(configurable.ConfigProperties.Select(ConvertConfigPropertyToDto).ToList());
            }
            return Task.FromResult(new List<PluginConfigPropertyDto>());
        }

        public Task<List<PluginConfigPropertyDto>> ApplyConfigAsync(string pluginId, string key, object? value)
        {
            // Settings can be secrets (an API key): the key is logged, never the value.
            Logger.Information("ApplyConfigAsync called: pluginId={PluginId}, key={Key}, valueType={ValueType}",
                pluginId, key, value?.GetType().Name ?? "null");

            var wrapper = _wrappers.FirstOrDefault(w => w.Id == pluginId);
            if (wrapper?.Plugin is IPluginConfigurable configurable)
            {
                // Find the property type so we can coerce the JSON-RPC value to the correct CLR type.
                // Values arrive as JToken over StreamJsonRpc and must be converted before
                // passing to the plugin's ApplyConfig.
                var prop = configurable.ConfigProperties.FirstOrDefault(p => p.Key == key);
                var nativeValue = prop != null ? CoerceValue(value, prop.Type) : value;

                Logger.Debug("Config coercion: key={Key}, rawType={RawType}, nativeType={NativeType}",
                    key, value?.GetType().Name ?? "null", nativeValue?.GetType().Name ?? "null");

                try
                {
                    configurable.ApplyConfig(key, nativeValue);
                    Logger.Information("Config applied successfully for key={Key}", key);
                    if (wrapper.ConfigFilePath == null)
                    {
                        SaveConfig(wrapper, configurable);
                    }
                }
                catch (Exception ex)
                {
                    // The exception's text can hold the value (a plugin saying what it couldn't take): only its kind is
                    // logged and reported, so a setting's value never leaves the settings call.
                    Logger.Error("Error applying config key {Key} on plugin {PluginId}: {Error}", key, pluginId, ex.GetType().Name);
                    NotifyError(pluginId, $"Config '{key}' apply failed ({ex.GetType().Name})");
                }
                // Return refreshed properties (ApplyConfig may change other properties)
                return Task.FromResult(configurable.ConfigProperties.Select(ConvertConfigPropertyToDto).ToList());
            }

            Logger.Warning("ApplyConfigAsync: plugin {PluginId} not found or not configurable", pluginId);
            return Task.FromResult(new List<PluginConfigPropertyDto>());
        }

        private object? CoerceValue(object? value, PluginConfigType type)
        {
            if (value is JToken jt)
            {
                try
                {
                    return type switch
                    {
                        PluginConfigType.Boolean => jt.Type == JTokenType.Boolean ? jt.Value<bool>()
                                                  : bool.TryParse(jt.ToString(), out var b) ? b : false,
                        PluginConfigType.Integer => jt.Type == JTokenType.Integer ? jt.Value<int>()
                                                  : jt.Type == JTokenType.Float ? (int)Math.Round(jt.Value<double>())
                                                  : int.TryParse(jt.ToString(), out var i) ? i : 0,
                        PluginConfigType.Double => jt.Type == JTokenType.Float || jt.Type == JTokenType.Integer
                                                  ? jt.Value<double>()
                                                  : double.TryParse(jt.ToString(), System.Globalization.NumberStyles.Float,
                                                      System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0.0,
                        PluginConfigType.String => jt.ToString(),
                        PluginConfigType.Choice => jt.ToString(),
                        _ => jt.ToString()
                    };
                }
                catch (Exception ex)
                {
                    Logger.Warning("Failed to coerce a {TokenType} value to {Type} ({Error}), returning default", jt.Type, type, ex.GetType().Name);
                    return type switch
                    {
                        PluginConfigType.Boolean => false,
                        PluginConfigType.Integer => 0,
                        PluginConfigType.Double => 0.0,
                        _ => jt.ToString()
                    };
                }
            }

            // StreamJsonRpc deserializes JSON integers as Int64 (long) and floats as double.
            // Coerce raw CLR values to the types plugins expect.
            try
            {
                return type switch
                {
                    PluginConfigType.Boolean => value is bool ? value : Convert.ToBoolean(value),
                    PluginConfigType.Integer => value is int ? value : Convert.ToInt32(value),
                    PluginConfigType.Double => value is double ? value : Convert.ToDouble(value),
                    PluginConfigType.String => value?.ToString(),
                    PluginConfigType.Choice => value?.ToString(),
                    _ => value
                };
            }
            catch (Exception ex)
            {
                Logger.Warning("Failed to coerce a {ValueType} value to {Type} ({Error}), returning default", value?.GetType().Name ?? "null", type, ex.GetType().Name);
                return type switch
                {
                    PluginConfigType.Boolean => false,
                    PluginConfigType.Integer => 0,
                    PluginConfigType.Double => 0.0,
                    PluginConfigType.String => value?.ToString(),
                    PluginConfigType.Choice => value?.ToString(),
                    _ => value
                };
            }
        }

        /// <summary>
        /// How long a plugin's <see cref="IPluginActionStates.Describe"/> is waited for: within the engine's 10 s for a
        /// call, so an action whose state didn't come in time is answered as not run, and never runs later.
        /// </summary>
        internal static readonly TimeSpan DescribeLimit = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long after its call reached the host an action may still start: within the engine's 10 s for a call, with
        /// time left for the answer, so an action the engine has answered as not run never starts.
        /// </summary>
        internal static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(8);

        /// <summary>
        /// How long shutdown waits for the actions under way to end before it closes the plugins: within the engine's
        /// budget for a host's stop.
        /// </summary>
        internal static readonly TimeSpan ActionStopLimit = TimeSpan.FromSeconds(1);

        // The runs under way, by plugin id and action, each run's end (what shutdown waits for) and whether shutdown has
        // begun (no run starts after it), all under _running's lock; the Describe asked of each action and not yet ended
        // (at most one at a time), and the actions whose Describe failed the last time it was asked (reported once a
        // streak).
        private readonly Dictionary<(string PluginId, string Method), int> _running = [];
        private readonly HashSet<Task> _runs = [];
        private bool _stopping;
        private readonly Dictionary<(string PluginId, string Method), Task<PluginActionState?>> _describing = [];
        private readonly HashSet<(string PluginId, string Method)> _describeFailing = [];

        /// <summary>Test seam: awaited by an action's worker as it begins, before its last check and the method.</summary>
        internal Func<Task>? BeforeActionStarts { get; set; }

        /// <summary>
        /// Runs the action when its state says it is enabled: on a task of its own, so a long action holds neither the
        /// connection's calls nor its other actions, the run counted in <see cref="ActionStateDto.Running"/> until it ends.
        /// The call is answered once that task has decided, immediately before the method: it starts the action only while
        /// the call isn't cancelled (the engine's deadline), within <see cref="StartLimit"/> of the call reaching the host
        /// and before shutdown has begun; else the answer reads not running and the action never runs. Only a method the
        /// plugin lists as an action (<see cref="PluginActionAttribute"/>, no parameters) is run; an action that returns a
        /// task is running until the task ends. A failure is the package's error, as in 1.x.
        /// </summary>
        public async Task<ActionStateDto> InvokeActionAsync(string pluginId, string methodName, CancellationToken cancellationToken = default)
        {
            var received = _time.GetTimestamp();
            if (FindAction(pluginId, methodName) is not (PluginWrapper wrapper, MethodInfo method, string name))
            {
                Logger.Warning("Plugin {PluginId} has no action {MethodName}", pluginId, methodName);
                return new ActionStateDto { Enabled = false };
            }

            var state = await StateOfAsync(wrapper, methodName, name);
            if (!state.Enabled)
                return state;

            var key = (pluginId, methodName);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(async () =>
            {
                try
                {
                    if (BeforeActionStarts is { } before)
                        await before();
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Starting action {MethodName} on plugin {PluginId} failed", methodName, pluginId);
                }

                // The last check, in the task that runs the method, immediately before it: a call the engine has given up
                // on, or one shutdown has overtaken, never starts its action.
                lock (_running)
                {
                    if (_stopping || cancellationToken.IsCancellationRequested || _time.GetElapsedTime(received) >= StartLimit)
                    {
                        started.SetResult(false);
                        return;
                    }
                    _running[key] = _running.GetValueOrDefault(key) + 1;
                    _runs.Add(ended.Task);
                }
                started.SetResult(true);

                try
                {
                    if (method.Invoke(wrapper.Plugin, null) is Task task)
                        await task;
                }
                catch (Exception ex)
                {
                    var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                    Logger.Error(cause, "Error invoking action {MethodName} on plugin {PluginId}", methodName, pluginId);
                    NotifyError(pluginId, $"Action '{methodName}' failed: {cause.Message}");
                }
                finally
                {
                    lock (_running)
                    {
                        if (--_running[key] == 0)
                            _running.Remove(key);
                        _runs.Remove(ended.Task);
                    }
                    ended.SetResult();
                }
            }, CancellationToken.None);

            state.Running = await started.Task;
            if (!state.Running)
                Logger.Information("Action {MethodName} on plugin {PluginId} wasn't started: its call ended or the host is stopping", methodName, pluginId);
            return state;
        }

        public async Task<ActionStateDto> DescribeActionAsync(string pluginId, string methodName, CancellationToken cancellationToken = default)
        {
            if (FindAction(pluginId, methodName) is not (PluginWrapper wrapper, _, string name))
                return new ActionStateDto { Enabled = false };
            return await StateOfAsync(wrapper, methodName, name);
        }

        /// <summary>The plugin <paramref name="pluginId"/> and its action <paramref name="methodName"/>, when it lists one by that name; else null.</summary>
        private (PluginWrapper Wrapper, MethodInfo Method, string Name)? FindAction(string pluginId, string methodName)
        {
            var wrapper = _wrappers.FirstOrDefault(w => w.Id == pluginId);
            var method = wrapper?.Plugin.GetType().GetMethod(methodName, Type.EmptyTypes);
            return method?.GetCustomAttributes(typeof(PluginActionAttribute), false).FirstOrDefault() is PluginActionAttribute attribute
                ? (wrapper!, method, attribute.DisplayName ?? "")
                : null;
        }

        /// <summary>
        /// The action's state as its plugin describes it (<see cref="IPluginActionStates"/>; a plugin without it: enabled,
        /// no state, no confirmation), cut to its limits, with whether it runs now. A Describe that throws or doesn't
        /// answer within <see cref="DescribeLimit"/> is taken as disabled, saying so, and reported once until it answers
        /// again; it runs on a task of its own, so one that hangs holds no other call.
        /// </summary>
        private async Task<ActionStateDto> StateOfAsync(PluginWrapper wrapper, string method, string name)
        {
            var dto = new ActionStateDto();
            if (wrapper.Plugin is IPluginActionStates states)
            {
                var key = (wrapper.Id, method);
                var asked = Describing(key, states);
                string? failure = null;
                try
                {
                    if (await Task.WhenAny(asked, Task.Delay(DescribeLimit, _time)) == asked)
                    {
                        var state = await asked;
                        dto.Enabled = state?.Enabled ?? true;
                        dto.State = state?.State is { Length: > 0 } line ? TableDtoConverter.Cut(line, PluginActionState.StateChars) : null;
                        if (state?.Confirm is { } confirm)
                        {
                            var title = string.IsNullOrEmpty(confirm.Title) ? (name.Length > 0 ? name : method) : confirm.Title;
                            dto.Confirm = new ActionConfirmDto
                            {
                                Title = TableDtoConverter.Cut(title, PluginActionConfirmation.TitleChars)!,
                                Text = TableDtoConverter.Cut(confirm.Text ?? "", PluginActionConfirmation.TextChars)!,
                            };
                        }
                    }
                    else
                    {
                        failure = $"its state didn't come within {DescribeLimit.TotalSeconds:0} seconds";
                    }
                }
                catch (Exception ex)
                {
                    failure = $"its state failed: {ex.Message}";
                }

                var report = false;
                lock (_describeFailing)
                {
                    if (failure == null)
                        _describeFailing.Remove(key);
                    else
                        report = _describeFailing.Add(key);
                }
                if (failure != null)
                {
                    dto = new ActionStateDto { Enabled = false, State = TableDtoConverter.Cut($"It can't run: {failure}", PluginActionState.StateChars) };
                    if (report)
                    {
                        Logger.Warning("Plugin {PluginId}'s action {MethodName}: {Failure}; reported once until it answers", wrapper.Id, method, failure);
                        NotifyError(wrapper.Id, $"Action '{method}': {failure}");
                    }
                }
            }

            lock (_running)
                dto.Running = _running.ContainsKey((wrapper.Id, method));
            return dto;
        }

        /// <summary>
        /// The plugin's Describe of the action <paramref name="key"/>, on a task of its own: the one already asked while it
        /// hasn't ended, so a Describe that hangs holds one task however often its action is asked about; a new one once it
        /// has. Its failure is observed here, whoever waited for it.
        /// </summary>
        private Task<PluginActionState?> Describing((string PluginId, string Method) key, IPluginActionStates states)
        {
            lock (_describing)
            {
                if (_describing.TryGetValue(key, out var asking) && !asking.IsCompleted)
                    return asking;
                var asked = Task.Run(() => states.Describe(key.Method));
                _describing[key] = asked;
                _ = asked.ContinueWith(t =>
                {
                    _ = t.Exception;
                    lock (_describing)
                    {
                        if (_describing.TryGetValue(key, out var current) && current == t)
                            _describing.Remove(key);
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return asked;
            }
        }

        public async Task ShutdownAsync()
        {
            Logger.Information("Shutdown requested");

            // No action starts from here on (one whose state is still being asked included); those under way get up to
            // ActionStopLimit to end before the plugins close.
            Task[] runs;
            lock (_running)
            {
                _stopping = true;
                runs = [.. _runs];
            }
            if (runs.Length > 0)
            {
                var all = Task.WhenAll(runs);
                using var limit = new CancellationTokenSource();
                if (await Task.WhenAny(all, Task.Delay(ActionStopLimit, _time, limit.Token)) != all)
                    Logger.Warning("{Count} action(s) still running at shutdown; closing the plugins", runs.Count(r => !r.IsCompleted));
                limit.Cancel();
            }

            if (_updateCts != null)
            {
                _updateCts.Cancel();
                if (_updateTask != null)
                {
                    try { await _updateTask; } catch (OperationCanceledException) { }
                }
                if (_perfUpdateTask != null)
                {
                    try { await _perfUpdateTask; } catch (OperationCanceledException) { }
                }
                _updateCts.Dispose();
            }

            foreach (var wrapper in _wrappers)
            {
                try
                {
                    await wrapper.StopAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error stopping plugin {PluginName}", wrapper.Name);
                }
            }

            // Dispose image writers (releases MMF handles)
            foreach (var writer in _imageWriters)
            {
                writer.Resized -= OnWriterResized;
                try { writer.Dispose(); } catch { }
            }
            _imageWriters.Clear();
            _writerMappings.Clear();

            // Signal the host process to exit gracefully by disposing the JsonRpc connection.
            // This causes jsonRpc.Completion in Program.cs to complete naturally.
            OnShutdownRequested?.Invoke();
        }

        private async Task UpdateLoopAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            var batches = new List<SensorUpdateBatchDto>();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await RunUpdateCycleAsync(batches, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Error(ex, "Error in update loop");
                }

                await Task.Delay(100, token);
            }
        }

        /// <summary>One cycle of the update loop: updates the sync plugins, then sends what changed and marks it sent.</summary>
        internal async Task RunUpdateCycleAsync(List<SensorUpdateBatchDto> batches, CancellationToken token)
        {
            // Call Update() on sync plugins (UpdateInterval <= 0)
            foreach (var wrapper in _wrappers)
            {
                if (wrapper.IsLoaded)
                {
                    try
                    {
                        wrapper.Update();
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex, "Error updating plugin {PluginName}", wrapper.Name);
                    }
                }
            }

            // A plugin's failed updates (its Update above, or UpdateAsync on its own task) are logged and reported to the
            // engine as its package's errors, once per streak of failures.
            foreach (var wrapper in _wrappers)
            {
                if (wrapper.TakeUpdateFailure() is { } failure)
                {
                    Logger.Warning(failure, "Plugin {PluginName} failed to update; reported once until an update succeeds", wrapper.Name);
                    await NotifyErrorAsync(wrapper.Id, $"Updating failed ({failure.GetType().Name})", token);
                }
            }

            // Snapshot and push changed values, in as many messages as keep each within IpcLimits.HostMessageBytes (by
            // JsonSize's estimate): the engine closes a host that sends one over its limit. Every update fits a message:
            // texts, units and tables are cut to the engine's limits before they are compared (SensorSnapshotManager).
            // Only ids aren't, and the engine refuses a plugin that lists a longer one than it takes.
            // The pictures (PluginImage) are read first: an entry that no longer holds one goes with the readings, as a
            // reading does, and each picture after them, in a message of its own.
            // Each message of readings waits for room on the host's wire (WireBudget: the readings' own budget and the
            // whole host's); one that finds none ends this cycle's readings, which go in a later cycle with their newest
            // values. The changes that waited longest go first, so later entries aren't kept back by earlier ones that
            // change all the time.
            var images = _snapshotManager.GetChangedImages(_wrappers.Where(w => w.IsLoaded));
            var changes = new List<(string PluginId, EntryUpdateDto Update)>();
            foreach (var wrapper in _wrappers)
            {
                if (!wrapper.IsLoaded) continue;
                foreach (var update in _snapshotManager.GetChangedEntries(wrapper))
                    changes.Add((wrapper.Id, update));
            }
            changes.AddRange(images.Where(i => !i.IsPicture).Select(i => (i.PluginId, i.Update)));

            batches.Clear();
            var bytes = JsonSize.MessageFixed;
            SensorUpdateBatchDto? batch = null;
            var held = false;
            foreach (var (pluginId, update) in changes.OrderBy(c => _snapshotManager.WaitingOrder(c.PluginId, c.Update)))
            {
                var size = JsonSize.Of(update);
                var batchSize = batch?.PluginId == pluginId ? 0 : JsonSize.BatchFixed + JsonSize.Of(pluginId);
                if (batches.Count > 0 && bytes + batchSize + size > IpcLimits.HostMessageBytes)
                {
                    var outcome = await SendReadingsAsync(batches, bytes, token);
                    if (outcome == Outcome.Failed) return;
                    batches.Clear();
                    if (outcome == Outcome.Held)
                    {
                        held = true;
                        break;
                    }
                    bytes = JsonSize.MessageFixed;
                    batch = null;
                    batchSize = JsonSize.BatchFixed + JsonSize.Of(pluginId);
                }

                if (batch == null || batch.PluginId != pluginId)
                {
                    batch = new SensorUpdateBatchDto { PluginId = pluginId, Updates = [] };
                    batches.Add(batch);
                }
                batch.Updates.Add(update);
                bytes += batchSize + size;
            }

            if (!held && batches.Count > 0 && await SendReadingsAsync(batches, bytes, token) == Outcome.Failed)
                return;

            // Then the pictures, each in a message of its own: an 8 MiB picture is about 10.7 MiB as base64, within the
            // engine's limit but over this host's share for a message of updates. Each is reserved within the rates
            // (IpcLimits) just before its send and held through it, so a send that stalls delays the rest.
            foreach (var picture in images)
            {
                if (!picture.IsPicture)
                    continue;
                if (!_snapshotManager.TryReserve(picture))
                    break;
                bool sent;
                try
                {
                    sent = await SendAsync([new SensorUpdateBatchDto { PluginId = picture.PluginId, Updates = [picture.Update] }], token);
                }
                finally
                {
                    _snapshotManager.Release(picture);
                }
                if (!sent)
                    return;
            }
        }

        private enum Outcome { Sent, Held, Failed }

        /// <summary>
        /// Sends one message of readings of <paramref name="bytes"/> (by the estimate) once the wire has room for it, held
        /// through the send: <see cref="Outcome.Held"/> when it has none now (nothing is sent, and the readings wait).
        /// </summary>
        private async Task<Outcome> SendReadingsAsync(List<SensorUpdateBatchDto> batches, long bytes, CancellationToken token)
        {
            if (!_snapshotManager.Wire.TryTakeReadings(bytes))
                return Outcome.Held;
            try
            {
                return await SendAsync(batches, token) ? Outcome.Sent : Outcome.Failed;
            }
            finally
            {
                _snapshotManager.Wire.Hold();
            }
        }

        /// <summary>
        /// Sends one message of updates and marks them sent once it was: only what the client was sent counts as sent, so
        /// an update that wasn't goes again next cycle. False when the send failed (the cycle sends no more).
        /// </summary>
        private async Task<bool> SendAsync(List<SensorUpdateBatchDto> batches, CancellationToken token)
        {
            if (!await NotifySensorUpdateAsync(batches, token))
                return false;
            foreach (var batch in batches)
            {
                _snapshotManager.MarkSent(batch.PluginId, batch.Updates);
            }
            return true;
        }

        private async Task PerformanceLoopAsync(CancellationToken token)
        {
            await Task.Delay(2000, token);

            var performances = new List<PluginPerformanceDto>();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var cpuPercent = GetCpuPercent();
                    performances.Clear();

                    foreach (var wrapper in _wrappers)
                    {
                        if (!wrapper.IsLoaded) continue;

                        performances.Add(new PluginPerformanceDto
                        {
                            PluginId = wrapper.Id,
                            UpdateTimeMilliseconds = wrapper.UpdateTimeMilliseconds,
                            CpuPercent = cpuPercent
                        });
                    }

                    if (performances.Count > 0)
                    {
                        NotifyPerformanceUpdate(performances);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Error(ex, "Error in performance loop");
                }

                await Task.Delay(2000, token);
            }
        }

        private double GetCpuPercent()
        {
            var proc = _currentProcess;
            if (proc == null) return 0;

            try
            {
                proc.Refresh();
                return CpuSample(proc.TotalProcessorTime);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Failed to compute CPU metrics");
            }

            return 0;
        }

        internal void StartCpuBaseline(TimeSpan cpuTime)
        {
            _lastCpuTime = cpuTime;
            _lastCheckStamp = _time.GetTimestamp();
        }

        /// <summary>The host's processor use since the last sample (or the baseline), given its total CPU time now.</summary>
        internal double CpuSample(TimeSpan cpuTime)
        {
            // The monotonic clock, not the wall clock: a step of the wall clock changes no figure.
            var now = _time.GetTimestamp();
            var elapsed = _time.GetElapsedTime(_lastCheckStamp, now);
            if (elapsed.TotalMilliseconds <= 0)
                return 0;

            var cpuPercent = CpuPercent(cpuTime - _lastCpuTime, elapsed);
            _lastCpuTime = cpuTime;
            _lastCheckStamp = now;
            return cpuPercent;
        }

        /// <summary>
        /// The host's processor use over <paramref name="elapsed"/>, per CPU: the CPU time it used in that wall time, in
        /// percent, so 100 is one busy CPU and the figure can pass 100 (0 when no time passed, never below 0). It needs no
        /// count of the machine's CPUs: the engine shows it as it counts its own CPU (per CPU off Windows, a share of every
        /// CPU's on Windows) and clamps it there.
        /// </summary>
        internal static double CpuPercent(TimeSpan cpuTime, TimeSpan elapsed) =>
            elapsed > TimeSpan.Zero ? Math.Max(0, cpuTime.TotalMilliseconds / elapsed.TotalMilliseconds * 100.0) : 0;

        /// <summary>
        /// A catalogue entry, cut as the engine cuts it (its name, a sensor's unit, a text, a table), with its value only
        /// while the entry with its value fits <paramref name="budget"/>: past that, a text is listed without its value and
        /// a table without its rows (or, if its columns alone don't fit, without its value), which the first update sends.
        /// The budget is reduced by what was listed.
        /// </summary>
        private EntryDto ConvertEntryToDto(IPluginData entry, ref long budget)
        {
            var dto = new EntryDto
            {
                Id = entry.Id,
                Name = TableDtoConverter.Cut(entry.Name, IpcLimits.NameChars)
            };

            if (entry is IPluginSensor sensor)
            {
                dto.Type = "sensor";
                var read = PluginSensorReading.Of(sensor);
                dto.SensorValue = new SensorValueDto
                {
                    Value = read.Value,
                    ValueMin = read.ValueMin,
                    ValueMax = read.ValueMax,
                    ValueAvg = read.ValueAvg,
                    Unit = TableDtoConverter.Cut(sensor.Unit, IpcLimits.NameChars)
                };
            }
            else if (entry is IPluginText text)
            {
                dto.Type = "text";
                dto.TextValue = TableDtoConverter.Cut(text.Value, IpcLimits.TextChars);
            }
            else if (entry is IPluginTable table)
            {
                dto.Type = "table";
                dto.TableValue = TableDtoConverter.Convert(table, IpcLimits.HostTableBytes).Table;
            }
            else if (entry is IPluginImage)
            {
                // Listed without its picture: the first cycle sends it, in a message of its own.
                dto.Type = "image";
            }

            if (JsonSize.Of(dto) > budget)
            {
                dto.TextValue = null;
                if (dto.TableValue != null)
                {
                    dto.TableValue.Rows = [];
                    // Its columns alone can still pass what is left (many tables with long column names): the whole value
                    // is left out then, and the first update sends it.
                    if (JsonSize.Of(dto) > budget)
                        dto.TableValue = null;
                }
            }

            budget -= JsonSize.Of(dto);
            return dto;
        }

        private static PluginConfigPropertyDto ConvertConfigPropertyToDto(PluginConfigProperty prop)
        {
            return new PluginConfigPropertyDto
            {
                Key = prop.Key,
                DisplayName = prop.DisplayName,
                Description = prop.Description,
                Type = prop.Type.ToString(),
                Value = prop.Value,
                MinValue = prop.MinValue,
                MaxValue = prop.MaxValue,
                Step = prop.Step,
                Options = prop.Options
            };
        }

        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        /// <summary>
        /// The plugin's settings file, <c>{settings folder}/{plugin id}.json</c>, in the folder the engine gave; null
        /// without one (settings kept in memory) or for an id that isn't a plain file name.
        /// </summary>
        private string? GetConfigFilePath(PluginWrapper wrapper)
        {
            if (_settingsDirectory == null) return null;
            var id = wrapper.Id;
            if (id.Length == 0 || id is "." or ".." || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || id.Contains('/') || id.Contains('\\'))
                return null;
            return Path.Combine(_settingsDirectory, $"{id}.json");
        }

        private void LoadAndApplyConfig(PluginWrapper wrapper, IPluginConfigurable configurable)
        {
            var configPath = GetConfigFilePath(wrapper);
            if (configPath == null || !File.Exists(configPath)) return;

            try
            {
                var json = File.ReadAllText(configPath);
                var stored = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (stored == null) return;

                var props = configurable.ConfigProperties;
                foreach (var kvp in stored)
                {
                    var prop = props.FirstOrDefault(p => p.Key == kvp.Key);
                    if (prop == null) continue;

                    try
                    {
                        // Each key on its own: a value that doesn't fit its setting's type (a setting whose type changed
                        // with an update, say) is skipped, and the keys after it still load.
                        if (!TryCoerceJsonElement(kvp.Value, prop.Type, out var value))
                        {
                            Logger.Warning("Skipped stored config key {Key} on plugin {PluginId}: a {Kind} value doesn't fit its type {Type}",
                                kvp.Key, wrapper.Id, kvp.Value.ValueKind, prop.Type);
                            continue;
                        }
                        configurable.ApplyConfig(kvp.Key, value);
                    }
                    catch (Exception ex)
                    {
                        // As in ApplyConfigAsync: the exception's text (or an inner one's) can hold the saved value.
                        Logger.Warning("Failed to apply stored config key {Key} on plugin {PluginId}: {Error}", kvp.Key, wrapper.Id, ex.GetType().Name);
                    }
                }

                Logger.Information("Loaded stored settings for plugin {PluginId} from {Path}", wrapper.Id, configPath);
            }
            catch (Exception ex)
            {
                // A parse error's text quotes the file, which holds the settings' values: only its kind is logged.
                Logger.Warning("Failed to load config file for plugin {PluginId}: {Error}", wrapper.Id, ex.GetType().Name);
            }
        }

        private void SaveConfig(PluginWrapper wrapper, IPluginConfigurable configurable)
        {
            var configPath = GetConfigFilePath(wrapper);
            if (configPath == null) return;
            try
            {
                var values = new Dictionary<string, object?>();
                foreach (var prop in configurable.ConfigProperties)
                {
                    values[prop.Key] = prop.Value;
                }
                WriteAtomically(configPath, JsonSerializer.SerializeToUtf8Bytes(values, _jsonOptions));
            }
            catch (Exception ex)
            {
                // A plugin's property getter can throw with a value in its text: only the exception's kind is logged.
                Logger.Warning("Failed to save config for plugin {PluginId}: {Error}", wrapper.Id, ex.GetType().Name);
            }
        }

        /// <summary>
        /// Writes <paramref name="bytes"/> to a temp file beside <paramref name="path"/>, flushes it and renames it over the
        /// path, so the file is always its previous content or the new, never a mix.
        /// </summary>
        private static void WriteAtomically(string path, byte[] bytes)
        {
            var folder = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(folder);
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        /// <summary>
        /// A stored value as its setting's type: a JSON value of the type's own kind, or a string or null read as before
        /// (parsed, else the type's default). False for any other kind, or a number the type can't hold (1.5 for an
        /// Integer): the value is skipped.
        /// </summary>
        private static bool TryCoerceJsonElement(JsonElement element, PluginConfigType type, out object? value)
        {
            var kind = element.ValueKind;
            var text = kind is JsonValueKind.String or JsonValueKind.Null;
            value = null;
            switch (type)
            {
                case PluginConfigType.Boolean when kind is JsonValueKind.True or JsonValueKind.False:
                    value = element.GetBoolean();
                    return true;
                case PluginConfigType.Boolean when text:
                    value = bool.TryParse(element.GetString(), out var b) && b;
                    return true;
                case PluginConfigType.Integer when kind == JsonValueKind.Number:
                    if (!element.TryGetInt32(out var number)) return false;
                    value = number;
                    return true;
                case PluginConfigType.Integer when text:
                    value = int.TryParse(element.GetString(), out var i) ? i : 0;
                    return true;
                case PluginConfigType.Double when kind == JsonValueKind.Number:
                    if (!element.TryGetDouble(out var real)) return false;
                    value = real;
                    return true;
                case PluginConfigType.Double when text:
                    value = double.TryParse(element.GetString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0.0;
                    return true;
                case PluginConfigType.Boolean or PluginConfigType.Integer or PluginConfigType.Double:
                    return false;
                default:
                    // String, Choice and any other type: a string, or null as the empty string.
                    if (!text) return false;
                    value = element.GetString() ?? "";
                    return true;
            }
        }

        private void NotifyPluginsLoaded(List<PluginMetadataDto> plugins, Dictionary<string, List<ContainerDto>> containers)
        {
            try
            {
                _jsonRpc?.NotifyAsync(nameof(IPluginClientCallback.OnPluginsLoaded), plugins, containers);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to notify plugins loaded");
            }
        }

        /// <summary>
        /// Sends the updates to the client; true once they are written to the connection. A client that is connected
        /// but stops reading blocks the write, and NotifyAsync takes no token: cancelling <paramref name="token"/> stops
        /// the wait (OperationCanceledException), not the write, so shutdown goes on and disposes the connection,
        /// which releases it. Nothing is marked sent then.
        /// </summary>
        private async Task<bool> NotifySensorUpdateAsync(List<SensorUpdateBatchDto> updates, CancellationToken token)
        {
            var jsonRpc = _jsonRpc;
            if (jsonRpc == null) return false;

            try
            {
                await jsonRpc.NotifyAsync(nameof(IPluginClientCallback.OnSensorUpdate), updates).WaitAsync(token);
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to notify sensor update");
                return false;
            }
        }

        /// <summary>
        /// Sends a plugin's error and waits for its write, as <see cref="NotifySensorUpdateAsync"/> does for updates, so the
        /// update loop's errors go out in order with its updates; one that can't be sent is logged and dropped.
        /// </summary>
        private async Task NotifyErrorAsync(string pluginId, string message, CancellationToken token)
        {
            var jsonRpc = _jsonRpc;
            if (jsonRpc == null || !Room(ErrorBytes(pluginId, message))) return;

            try
            {
                await jsonRpc.NotifyAsync(nameof(IPluginClientCallback.OnPluginError), pluginId, message).WaitAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to notify plugin error");
            }
        }

        /// <summary>
        /// Whether a report (an error, a resize, the processor use) of <paramref name="bytes"/> by the estimate may go now:
        /// it takes room on the host's wire, and is dropped when there is none (the host's log keeps errors, and the engine
        /// a package's newest only).
        /// </summary>
        private bool Room(long bytes) => _jsonRpc != null && _snapshotManager.Wire.TryTake(bytes);

        private static long ErrorBytes(string pluginId, string message) => JsonSize.MessageFixed + JsonSize.Of(pluginId) + JsonSize.Of(message);

        private void NotifyError(string pluginId, string message)
        {
            if (!Room(ErrorBytes(pluginId, message))) return;
            try
            {
                _jsonRpc?.NotifyAsync(nameof(IPluginClientCallback.OnPluginError), pluginId, message);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to notify plugin error");
            }
        }

        private void OnWriterResized(PluginImageWriter writer)
        {
            if (!_writerMappings.TryGetValue(writer, out var mapping)) return;

            var descriptor = new ImageDescriptorDto
            {
                Id = mapping.ImageId,
                Name = mapping.ImageId,
                Width = writer.Width,
                Height = writer.Height,
                MmfName = writer.MmfName,
                BufferSize = PluginImageWriter.ComputeMmfSize(writer.Width, writer.Height)
            };

            Logger.Information("Image resized: {PluginId}/{ImageId} -> {W}x{H} (MMF: {MmfName})",
                mapping.PluginId, mapping.ImageId, writer.Width, writer.Height, writer.MmfName);

            NotifyImageResize(mapping.PluginId, descriptor);
        }

        private void NotifyImageResize(string pluginId, ImageDescriptorDto descriptor)
        {
            if (!Room(JsonSize.MessageFixed + JsonSize.Of(pluginId) + 256 + JsonSize.Of(descriptor.Id) + JsonSize.Of(descriptor.Name) + JsonSize.Of(descriptor.MmfName))) return;
            try
            {
                _jsonRpc?.NotifyAsync(nameof(IPluginClientCallback.OnImageResize), pluginId, descriptor);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to notify image resize");
            }
        }

        private void NotifyPerformanceUpdate(List<PluginPerformanceDto> performances)
        {
            // {"PluginId":,"UpdateTimeMilliseconds":n,"CpuPercent":n} and a comma: within 128 bytes beside its id.
            if (!Room(JsonSize.MessageFixed + performances.Sum(p => 128 + JsonSize.Of(p.PluginId)))) return;
            try
            {
                _jsonRpc?.NotifyAsync(nameof(IPluginClientCallback.OnPerformanceUpdate), performances);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to notify performance update");
            }
        }
    }
}
