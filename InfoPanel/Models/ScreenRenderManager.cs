using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Models
{

    // Manages version tracking for display items to detect updates and avoid unnecessary redraws.
    public static class ScreenRenderManager
    {
        private static readonly ILogger Logger = Log.ForContext("SourceContext", nameof(ScreenRenderManager));

        // プロファイルに関する最大目標FPS、バージョンを管理する内部クラス
        private class ProfileInfo
        {
            // プロファイルに紐づくバージョン情報を管理
            private long _version;
            // プロファイルのバージョンの最終更新時間
            private long _lastUpdateTimeFrequency = Stopwatch.GetTimestamp();
            // プロファイルのバージョンの更新間隔
            private long _updateIntervalFrequency = Stopwatch.Frequency; // 初期値は 1FPS とする
            // 更新間隔（_updateIntervalFrequency）の読み書きを保護するロックオブジェクト
            private readonly object _fpsLock = new();

            // プロファイルに紐づくバージョン情報を更新する
            public void IncVersion()
            {
                Interlocked.Increment(ref _version);
            }
            // プロファイルに紐づくバージョン情報を取得する
            public long GetVersion()
            {
                return Interlocked.Read(ref _version);
            }
            // 更新間隔(最大FPS)に基づきバージョンの更新が必要な場合は、バージョンを更新する
            // 更新の有無にかかわらず、次回バージョン更新までの残り時間（Stopwatch ティック数）を返す。CPU 負荷軽減のため DateTime を使わず long 値で計算する。
            public long UpdateVersionIfNeeded()
            {
                var currentTimeFrequency = Stopwatch.GetTimestamp();
                var interval = Volatile.Read(ref _updateIntervalFrequency);
                var elapsedTime = currentTimeFrequency - _lastUpdateTimeFrequency;
                if (elapsedTime >= interval)
                {
                    IncVersion();
                    _lastUpdateTimeFrequency = currentTimeFrequency;
                    return interval;
                }
                else
                {
                    return interval == long.MaxValue ? long.MaxValue : _lastUpdateTimeFrequency - currentTimeFrequency + interval;
                }
            }            // 指定された目標FPSから更新間隔（Stopwatch ティック数）を計算して設定する
            public void UpdateMaxFps(int targetFps)
            {
                lock (_fpsLock)
                {
                    var targetFrameTimeFrequency = targetFps == 0 ? long.MaxValue : Stopwatch.Frequency / targetFps;
                    Volatile.Write(ref _updateIntervalFrequency, targetFrameTimeFrequency);
                }
            }
        }

        // HWiNFO のセンサー値の読み取り頻度(ms)
        public static int HWPoolIntervalMilliseconds { get; } = 500;
        // LibreHardwareMonitor のセンサー値の読み取り頻度(ms)
        public static int LibrePoolIntervalMilliseconds { get; } = 2000;
        // InfoPanel の最小、最大目標FPS
        // 0 FPS 静的なアイテムを表示する際に利用する
        // 60 FPS を超えるパネルの定義は無いため、60 FPS を最大値とする。パネルの種類が増えた際には、最大FPSを変更する必要があるかもしれない。
        public const int MinFps = 0;
        public const int MaxFps = 60;
        // グラフの更新間隔の最低間隔値、最長間隔値
        public const int MinTargetGraphUpdateRate = 100;
        public const int MaxTargetGraphUpdateRate = 3000;
        // パネルに関する情報を取得する際に基底クラスがないためリフレクションで取得するためのベンダ名定義
        private static string[] PanelVendors { get; } = ["Beada", "Jl", "Jonsbo", "LianLi", "Thermalright", "Thermaltake", "Turing", "Vmax"];

        // プロファイルの一覧
        private static ObservableCollection<Profile> Profiles { get; } = ConfigModel.Instance.Profiles;
        // アプリケーションの設定
        private static Settings Settings { get; } = ConfigModel.Instance.Settings;
        // 有効となっているパネルに関連づいているプロファイルの一覧
        // 無効および関連づいていないプロファイルは管理しない。パネル設定を変更すると本情報も更新される
        private static ConcurrentDictionary<Guid, Profile> EnabledProfiles { get; set; } = new();
        // パネルに基づき関連づいているプロファイルの最大FPSを管理するディクショナリ
        private static ConcurrentDictionary<Guid, int> MaxTargetFrameRateProfileByPanel { get; set; } = new();
        // DisplayItem に基づき関連づいているプロファイルの最大FPSを管理するディクショナリ
        private static ConcurrentDictionary<Guid, int> MaxTargetFrameRateProfileByDisplayItem { get; set; } = new();
        // 管理対象のディスプレイアイテムを格納するディクショナリ
        private static ConcurrentDictionary<Guid, DisplayItem> DisplayItems { get; set; } = new();
        // DisplayItem と SensorID を関連づけるディクショナリ
        private static ConcurrentDictionary<Guid, string> DisplayItemIdToSensorId { get; set; } = new();
        // センサーID に紐づいている対象ディスプレイアイテムを管理するディクショナリ
        // 一つのセンサーが複数のディスプレイアイテムに利用されているかを把握するために使う
        private readonly static ConcurrentDictionary<string, ConcurrentDictionary<Guid, DisplayItem>> SensorIdToDisplayItems = new();

        // 低頻度の更新が想定されるディスプレイアイテムのGUIDとそのディスプレイアイテムを格納するディクショナリ
        private static ConcurrentDictionary<Guid, DisplayItem> LowUpdateFrequencyDisplayItemIds { get; set; } = new();
        // 低頻度更新ディスプレイアイテムの最終取得値(string)を管理するためのディクショナリ
        private static ConcurrentDictionary<Guid, string> LowUpdateFrequencyDisplayItemStringValues { get; set; } = new();
        // 低頻度更新ディスプレイアイテムの最終取得値(long)を管理するためのディクショナリ
        private static ConcurrentDictionary<Guid, long> LowUpdateFrequencyDisplayItemLongValues { get; set; } = new();
        // 低頻度更新ディスプレイアイテムの最終取得値を取得した時刻
        static long LastLowUpdateFrequencyCheckTime { get; set; } = Stopwatch.GetTimestamp();
        // プロファイルのバージョンや必要なFPSを管理するためのディクショナリ
        private static ConcurrentDictionary<Guid, ProfileInfo> ProfileInfos { get; set; } = new();
        // 起動時やユーザの操作に伴いパネルやディスプレイアイテムの変更を検出するためのフラグ
        private static int _profilesDirty;

        // 各プロファイルのバージョンを目標FPSに従い更新する (このクラスのメイン処理、InfoPanel 起動時に呼び出される)
        public static async Task UpdateProfilesVersions(CancellationToken cancellationToken)
        {
            CheckUpdate();
            while (!cancellationToken.IsCancellationRequested)
            {
                CheckProfilesOrSettingsDirty();
                // 次回ループまでの待機時間（Stopwatch ティック数）を格納する。
                long nextUpdateFrequency = long.MaxValue;
                var currentTimeFrequency = Stopwatch.GetTimestamp();

                // 低頻度更新ディスプレイアイテムを評価し、値が変化すれば対象プロファイルのバージョンをインクリメントする（1秒間隔で実行）
                if (currentTimeFrequency - LastLowUpdateFrequencyCheckTime >= Stopwatch.Frequency)
                {
                    try
                    {
                        LastLowUpdateFrequencyCheckTime = currentTimeFrequency;
                        foreach (KeyValuePair<Guid, DisplayItem> kvp in LowUpdateFrequencyDisplayItemIds)
                        {
                            switch (kvp.Value)
                            {
                                case (TextDisplayItem text):
                                    _ = LowUpdateFrequencyDisplayItemStringValues.TryGetValue(kvp.Key, out var lastValue);

                                    var currentValue = text.EvaluateText();
                                    if (lastValue != currentValue)
                                    {
                                        // 値が変化したため対象プロファイルのバージョンをインクリメントする
                                        if (ProfileInfos.TryGetValue(text.ProfileGuid, out var profileInfo))
                                        {
                                            profileInfo.IncVersion();
                                            LowUpdateFrequencyDisplayItemStringValues[kvp.Key] = currentValue;
                                        }
                                    }
                                    break;
                                case (ImageDisplayItem image):
                                    // URL の場合は更新間隔でリクエストが発生できるよう初期値として現在時刻を代入
                                    // FPSのチェック時に更新間隔の基準点がずれないよう GetOrAdd で代入
                                    if (image.Type == ImageDisplayItem.ImageType.URL)
                                    {
                                        _ = LowUpdateFrequencyDisplayItemLongValues.GetOrAdd(image.Guid, currentTimeFrequency);
                                    }

                                    _ = LowUpdateFrequencyDisplayItemLongValues.TryGetValue(kvp.Key, out var lastUpdateTimeFrequency);

                                    if ((currentTimeFrequency - lastUpdateTimeFrequency) / Stopwatch.Frequency >= image.RefreshInterval)
                                    {
                                        // Http画像の更新間隔なので ProfileInfo のバージョンを更新しHttpのリクエストさせる
                                        if (ProfileInfos.TryGetValue(image.ProfileGuid, out var profileInfo))
                                        {
                                            profileInfo.IncVersion();
                                            LowUpdateFrequencyDisplayItemLongValues[kvp.Key] = currentTimeFrequency;
                                        }

                                    }
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "An error occurred while checking low-frequency items. Please verify the TextDisplayItem or ImageDisplayItem. (The DateTime format may be incorrect.)");
                    }
                }

                // 各プロファイルのバージョン更新の要否を確認し、次回更新までの最短待機時間を求める
                foreach (var profileId in ProfileInfos.Keys)
                {
                    if (!ProfileInfos.TryGetValue(profileId, out var profileInfo)) continue;
                    nextUpdateFrequency = Math.Min(nextUpdateFrequency, profileInfo.UpdateVersionIfNeeded());
                }
                // 次回更新までの最短待機時間を計算して待機する
                // 最低10msは待ち、最大待ち時間は 1000msとする。これは低頻度更新オブジェクトの更新有無を確認するための更新間隔とする
                await Task.Delay((int)Math.Clamp(Math.Ceiling(nextUpdateFrequency * 1000.0 / Stopwatch.Frequency), 10.0, 1000.0), cancellationToken);
            }
        }

        // 対象のプロファイルのバージョンを返す。プロファイルが存在しない場合は 0 を返す
        public static long GetProfileVersion(Guid profileId)
        {
            if (ProfileInfos.TryGetValue(profileId, out var profileInfo))
            {
                var version = profileInfo.GetVersion();
                // Logger.Debug("GetProfileVersion : {profileId}:{version}", profileId, version);
                return version;
            }
            return 0;
        }

        // 対象のプロファイルのバージョンを更新する
        public static void IncProfileVersion(Guid profileId)
        {
            // プロファイルが存在しない場合は新たに作成する
            var profileInfo = ProfileInfos.GetOrAdd(profileId, _ => new ProfileInfo());
            profileInfo.IncVersion();
            Logger.Debug("IncProfileVersion : {profileId}:{version}", profileId, profileInfo.GetVersion());
        }

        // センサーID の更新通知を受けてセンサーIDと関連づいているプロファイルのバージョンを更新する
        public static void IncProfileVersionByItem(string itemKey)
        {
            if (string.IsNullOrEmpty(itemKey)) return;
            if (!SensorIdToDisplayItems.TryGetValue(itemKey, out var displayItems)) return;

            Logger.Debug("IncProfileVersionByItem : {itemKey}", itemKey);

            foreach (var displayItem in displayItems.Values)
            {
                IncProfileVersion(displayItem.ProfileGuid);
            }
        }

        // 起動時・UI 操作に伴い内部情報の再構築が必要になった場合に呼ばれる（他クラスから呼び出される）
        public static void SetProfilesOrSettingsDirty()
        {
            Interlocked.Exchange(ref _profilesDirty, 1);
        }

        private static void CheckProfilesOrSettingsDirty()
        {
            if (Interlocked.Exchange(ref _profilesDirty, 0) == 1)
            {
                CheckUpdate();
            }
        }

        // 内部情報を更新するための処理
        private static void CheckUpdate()
        {
            // パネル関連の情報更新
            CheckUsedProfilesAndMaxFpsByPanel();

            // ディスプレイアイテムの一覧をコピー
            var lastDisplayItems = new ConcurrentDictionary<Guid, DisplayItem>(DisplayItems);
            // プロファイル関連の情報をコピー（管理対象外プロファイルの有無確認のため）
            var lastEnabledProfiles = new ConcurrentDictionary<Guid, Profile>(EnabledProfiles);
            // プロファイル関連の情報更新
            foreach (var profile in EnabledProfiles.Values)
            {
                CheckProfileInfoAndMaxFpsByProfile(profile, lastDisplayItems, lastEnabledProfiles);
            }
            // ディスプレイアイテムの不要分を削除
            foreach (var displayItem in lastDisplayItems.Values)
            {
                RemoveDisplayItem(displayItem);
            }
            // プロファイルの不要分を削除
            foreach (var profileId in lastEnabledProfiles.Keys)
            {
                _ = ProfileInfos.TryRemove(profileId, out _);
                _ = EnabledProfiles.TryRemove(profileId, out _);
            }

            // 目標FPS は DisplayItem 由来の最大FPS とパネル由来の最大FPS のうち小さい方を採用し、各プロファイルのバージョンを更新する
            foreach (var enableProfileId in EnabledProfiles.Keys)
            {
                var profileInfo = ProfileInfos.GetOrAdd(enableProfileId, _ => new ProfileInfo());
                profileInfo.UpdateMaxFps(Math.Min(MaxTargetFrameRateProfileByDisplayItem[enableProfileId], MaxTargetFrameRateProfileByPanel[enableProfileId]));
                profileInfo.IncVersion();
            }
        }

        // DisplayItem の GUID を SensorIdToDisplayItems（センサーID → DisplayItem マッピング）に登録する
        public static void AddDisplayItem(DisplayItem displayItem)
        {
            _ = DisplayItems.GetOrAdd(displayItem.Guid, displayItem);
            string sensorId = GetSensorIdFromDisplayItem(displayItem);

            if (sensorId == string.Empty) return;
            var displayItems = SensorIdToDisplayItems.GetOrAdd(sensorId, _ => new ConcurrentDictionary<Guid, DisplayItem>());
            displayItems.GetOrAdd(displayItem.Guid, displayItem);
            Logger.Debug("AddDisplayItem : {sensorId} : {displayItem}", sensorId, displayItem.Guid);

            DisplayItemIdToSensorId.GetOrAdd(displayItem.Guid, sensorId);
            Logger.Debug("AddDisplayItem : {displayItem} : {sensorId}", displayItem.Guid, sensorId);
        }


        // DisplayItem の GUID をキーに SensorIdToDisplayItems（センサーID → DisplayItem マッピング）から削除する
        public static void RemoveDisplayItem(DisplayItem displayItem)
        {
            string sensorId = GetSensorIdFromDisplayItem(displayItem);
            if (sensorId != string.Empty)
            {
                if (SensorIdToDisplayItems.TryGetValue(sensorId, out var displayItems))
                {
                    _ = displayItems.TryRemove(displayItem.Guid, out _);
                    Logger.Debug("RemoveDisplayItem : {sensorId} : {displayItem}", sensorId, displayItem.Guid);
                }
            }
            DisplayItemIdToSensorId.TryRemove(displayItem.Guid, out _);
            Logger.Debug("RemoveDisplayItem : {displayItem} : {sensorId}", displayItem.Guid, sensorId);

            _ = LowUpdateFrequencyDisplayItemIds.TryRemove(displayItem.Guid, out _);
            _ = LowUpdateFrequencyDisplayItemStringValues.TryRemove(displayItem.Guid, out _);
            _ = LowUpdateFrequencyDisplayItemLongValues.TryRemove(displayItem.Guid, out _);
            _ = DisplayItems.TryRemove(displayItem.Guid, out _);
        }

        // 最新のディスプレイアイテム一覧を基に内部情報更新
        private static void CheckProfileInfoAndMaxFpsByProfile(Profile profile, ConcurrentDictionary<Guid, DisplayItem> lastDisplayItems, ConcurrentDictionary<Guid, Profile> lastEnabledProfiles)
        {
            int tempMaxFps = 0;
            var displayItems = SharedModel.Instance.GetProfileDisplayItemsCopy(profile);
            foreach (var displayItem in displayItems)
            {
                if (displayItem.Hidden) continue;
                if (displayItem is GroupDisplayItem group)
                {
                    foreach (var childDisplayItem in group.DisplayItems)
                    {
                        if (childDisplayItem.Hidden) continue;
                        var fps = GetFpsByDisplayItem(childDisplayItem);
                        if (fps > tempMaxFps)
                        {
                            tempMaxFps = fps;
                        }
                        AddDisplayItem(childDisplayItem);
                        _ = lastDisplayItems.TryRemove(childDisplayItem.Guid, out _);
                        _ = lastEnabledProfiles.TryRemove(profile.Guid, out _);
                    }
                }
                else
                {
                    var fps = GetFpsByDisplayItem(displayItem);
                    if (fps > tempMaxFps)
                    {
                        tempMaxFps = fps;
                    }
                    AddDisplayItem(displayItem);
                    _ = lastDisplayItems.TryRemove(displayItem.Guid, out _);
                }
            }
            MaxTargetFrameRateProfileByDisplayItem[profile.Guid] = tempMaxFps;
        }

        // 最新のパネル一覧を基に内部情報更新
        private static void CheckUsedProfilesAndMaxFpsByPanel()
        {
            Dictionary<Guid, int> profileIndex = new();
            Dictionary<Guid, int> maxFrameRateByProfile = new();
            var lastEnabledProfiles = new ConcurrentDictionary<Guid, Profile>(EnabledProfiles);
            for (int i = 0; i < Profiles.Count; i++)
            {
                profileIndex.TryAdd(Profiles[i].Guid, i);
            }
            foreach (var vendor in PanelVendors)
            {
                // 各パネルベンダごとの設定を確認し有効なら後続を実行
                string propName = $"{vendor}PanelMultiDeviceMode";
                var prop = typeof(Settings).GetProperty(propName);
                if (prop is null)
                {
                    Logger.Debug("Property {propName} was not found.", propName);
                    continue;
                }
                try
                {
                    var value = prop.GetValue(Settings);
                    if (value is bool result && !result) continue;
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Get error {propName}.", propName);
                    continue;
                }

                // 各パネルが有効なら参照している ProfileGuid と Profile に関連づいている TargetFrameRate の最大値をディクショナリに追加する
                propName = $"{vendor}PanelDevices";
                prop = typeof(Settings).GetProperty(propName);

                if (prop is null)
                {
                    Logger.Debug("Property {propName} was not found.", propName);
                    continue;
                }
                try
                {
                    var value = prop.GetValue(Settings);
                    if (value is null)
                    {
                        
                        continue;
                    }
                    Logger.Debug("{propName} was found.", propName);

                    if (value is not System.Collections.IEnumerable devices)
                    {
                        Logger.Debug("{propName} is not enumerable.", propName);
                        continue;
                    }

                    foreach (var device in devices)
                    {
                        var type = device.GetType();

                        prop = type.GetProperty("Enabled");
                        if (prop is null)
                        {
                            Logger.Debug("{prop} was not found.", prop);
                            continue;
                        }
                        if (prop.GetValue(device) is not bool enabled || !enabled)
                        {
                            Logger.Debug("{device} was disabled.", device);
                            continue;
                        }

                        prop = type.GetProperty("ProfileGuid");
                        if (prop is null)
                        {
                            Logger.Debug("{device} {prop} was not found.", device, prop);
                            continue;
                        }
                        var profileGuid = prop.GetValue(device);
                        if (profileGuid is null)
                        {
                            Logger.Debug("{device} {prop} was null.", device, prop);
                            continue;
                        }
                        if (profileIndex.TryGetValue((Guid)profileGuid, out var index))
                        {
                            _ = EnabledProfiles.GetOrAdd((Guid)profileGuid, Profiles[index]);
                            _ = lastEnabledProfiles.TryRemove((Guid)profileGuid, out _);
                            Logger.Debug("{vendor} {panel} Enabled → {ProfileGuid}", vendor, device, profileGuid);
                        }
                        else
                        {
                            Logger.Debug("{vendor} {panel} Enabled → but, not fount {ProfileGuid}", vendor, device, profileGuid);
                            continue;
                        }

                        prop = type.GetProperty("TargetFrameRate");
                        if (prop is null)
                        {
                            Logger.Debug("{device} {prop} was not found.", device, prop);
                            continue;
                        }
                        var targetFrameRate = prop.GetValue(device);
                        if (targetFrameRate is null)
                        {
                            Logger.Debug("{device} {prop} was null.", device, prop);
                            continue;
                        }
                        if (maxFrameRateByProfile.TryGetValue((Guid)profileGuid, out var tempMaxFrameRate))
                        {
                            if ((int)targetFrameRate > tempMaxFrameRate)
                            {
                                maxFrameRateByProfile[(Guid)profileGuid] = (int)targetFrameRate;
                            }
                        }
                        else
                        {
                            maxFrameRateByProfile[(Guid)profileGuid] = (int)targetFrameRate;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Get error {propName}", propName);
                    continue;
                }
            }
            // 参照されなくなった Profile の情報を削除する
            foreach (var profileId in lastEnabledProfiles.Keys)
            {
                _ = EnabledProfiles.TryRemove(profileId, out _);
                _ = MaxTargetFrameRateProfileByPanel.TryRemove(profileId, out _);
                _ = MaxTargetFrameRateProfileByDisplayItem.TryRemove(profileId, out _);
            }
            foreach (var profileId in EnabledProfiles.Keys)
            {
                // TargetFrameRate を更新する
                MaxTargetFrameRateProfileByPanel[profileId] = maxFrameRateByProfile[profileId];
            }
        }

        // ディスプレイアイテムから SensorId を取得する
        private static string GetSensorIdFromDisplayItem(DisplayItem displayItem)
        {
            if (displayItem is null) return string.Empty;
            Logger.Debug("GetSensorIdFromDisplayItem : item class {class} , base class {baseclass}", displayItem.GetType().FullName, displayItem.GetType().BaseType!.FullName);
            return displayItem switch
            {
                SensorDisplayItem sensor => GetSensorKey(sensor),
                ChartDisplayItem chart => GetSensorKey(chart),
                HttpImageDisplayItem httpImage => $"{httpImage.PluginSensorId}",
                _ => $"{displayItem.Guid}",
            };
        }

        private static string GetSensorKey(ISensorItem sensor)
        {
            return sensor switch
            {
                IHwInfoSensorItem hw when hw.SensorType == Enums.SensorType.HwInfo => $"{hw.HwInfoRemoteIndex}:{hw.Id}:{hw.Instance}:{hw.EntryId}",
                ILibreSensorItem libre when libre.SensorType == Enums.SensorType.Libre => $"{libre.LibreSensorId}",
                IPluginSensorItem plugin when plugin.SensorType == Enums.SensorType.Plugin => $"{plugin.PluginSensorId}",
                _ => string.Empty,
            };
        }

        // ディスプレイアイテムから更新に必要な目標FPSを取得する。
        private static int GetFpsByDisplayItem(DisplayItem displayItem)
        {
            int fps = MinFps; // デフォルトは 0 FPS とする
            switch (displayItem)
            {
                case ChartDisplayItem chart:
                    switch (chart)
                    {
                        case GraphDisplayItem graph:
                            // GUI 上では制御されているが、設定ファイルが手編集されて想定外の値とならないよう範囲をクランプする
                            // ※ MinTargetGraphUpdateRate / MaxTargetGraphUpdateRate は GUI 上で設定可能な最小・最大値
                            var targetGraphUpdateRate = Math.Clamp(ConfigModel.Instance.Settings.TargetGraphUpdateRate, MinTargetGraphUpdateRate, MaxTargetGraphUpdateRate);
                            fps = (int)Math.Ceiling(1000.0 / targetGraphUpdateRate);
                            break;
                        default:
                            // BarDisplayItem, DoughnutDisplayItem の2種類は、アニメーションの有無で目標FPSを切り替える
                            // アニメーション有効時は最大FPS（MaxFps）で更新する
                            if (chart.Animation)
                            {
                                fps = MaxFps;
                            }
                            else
                            {
                                // アニメーションがない場合は対象センサーの更新頻度に紐づき更新されるためここでは 0 FPSでよい
                                fps = MinFps;
                            }
                            break;
                    }
                    break;
                case CalendarDisplayItem calendar:
                    // 日付はほぼ更新されないため低頻度更新ディスプレイアイテムとして管理
                    AddLowFrequencyDicByDisplayItem(calendar);
                    break;
                case ClockDisplayItem clock:
                    // FPS を確認するためにフォーマットをチェック (秒以下を表示しない場合は低頻度更新ディスプレイアイテムとして管理)
                    // フォーマットにミリ秒以下が何桁含まれているかをチェック
                    int fractionalSecondDigits = GetFormatDigits(clock.Format, 'f');
                    // フォーマットに秒が含まれているかをチェック
                    int secondDigits = GetFormatDigits(clock.Format, 's');
                    if (fractionalSecondDigits >= 2)
                    {
                        // ミリ秒以下が2桁以上はMaxFps
                        fps = MaxFps;
                    }
                    else if (fractionalSecondDigits == 1)
                    {
                        // ミリ秒以下が1桁は10FPS
                        fps = 10;
                    }
                    else if (secondDigits > 0)
                    {
                        // 秒が含まれていたら1FPS
                        fps = 1;
                    }
                    else
                    {
                        fps = MinFps;
                    }
                    if (fps == MinFps)
                    {
                        AddLowFrequencyDicByDisplayItem(clock);
                    }
                    else
                    {
                        RemoveLowFrequencyDicByDisplayItem(clock);
                    }
                    break;
                case GaugeDisplayItem:
                    // GaugeDisplayItem は SensorDisplayItem の更新により判定されるため、このクラス自身では FPS は判定しない
                    break;
                case ImageDisplayItem image:
                    switch (image)
                    {
                        case HttpImageDisplayItem httpImage:
                            // 対象センサー、プラグインの更新に伴い更新されるため このクラス自身ではFPSは判定しない
                            break;
                        default:
                            switch (image.Type)
                            {
                                case ImageDisplayItem.ImageType.URL:
                                    if (image.RefreshInterval == 0)
                                    {
                                        // 0 は更新が必要ないため MinFps のまま
                                        RemoveLowFrequencyDicByDisplayItem(image);
                                    }
                                    else
                                    {
                                        // 1 以上の場合は 1 秒以上の間隔で更新される可能性があるため、低頻度更新ディスプレイアイテムとして管理
                                        // 設定された更新間隔で描画させることでリクエストを発生させる
                                        AddLowFrequencyDicByDisplayItem(image);
                                    }

                                    break;
                                default:
                                    fps = image.TargetFps;
                                    break;
                            }
                            break;
                    }
                    break;
                case SensorDisplayItem sensor:
                    // sensor は HWiNFO、LibreHardwareMonitor、Plugin により収集周期が異なるため、種類ごとに処理を分岐させる
                    switch (sensor)
                    {
                        case IHwInfoSensorItem hw when hw.SensorType == Enums.SensorType.HwInfo:
                            fps = (int)Math.Ceiling(1000.0 / HWPoolIntervalMilliseconds);
                            break;
                        case ILibreSensorItem libre when libre.SensorType == Enums.SensorType.Libre:
                            fps = (int)Math.Ceiling(1000.0 / LibrePoolIntervalMilliseconds);
                            break;
                        case IPluginSensorItem plugin when plugin.SensorType == Enums.SensorType.Plugin:
                            // プラグインのセンサーは、プラグイン側で更新頻度を決定するため、ここでは MinFps（0 FPS）ととする
                            // プラグイン側で更新された際に対象プロファイルのバージョンが更新される
                            break;
                        default:
                            // 未対応のセンサー種別（新インタフェース追加時にここに case を追加）
                            break;
                    }
                    break;

                case ShapeDisplayItem shape:
                    // ShapeDisplayItem はグラデーションのアニメーション速度により更新頻度を変更する
                    if (shape.GradientAnimationSpeed == 0)
                    {
                        // アニメーション無効の場合は 0 FPS
                    }
                    else
                    {
                        // アニメーション有効時は GradientAnimationSpeed に応じて FPS を設定
                        // GradientAnimationSpeed は 1 - 3600 で設定可能だが、MaxFps（60）を超える値は実際の描画能力を超えているため MaxFps でクランプする
                        fps = Math.Clamp(shape.GradientAnimationSpeed, 1, MaxFps);
                    }
                    break;

                default:
                    // TextDisplayItem、GroupDisplayItem、GuidDisplayItem は設定変更時のみ更新される
                    // 変更した場合設定変更として再描画発生するため問題なし。
                    // ディスプレイアイテムの種類が追加された場合はこの switch の分岐を検討する
                    break;
            }
            return fps;
        }

        // 低頻度更新ディスプレイアイテムを追加するための共通メソッド
        private static void AddLowFrequencyDicByDisplayItem(DisplayItem displayItem)
        {
            _ = LowUpdateFrequencyDisplayItemIds.GetOrAdd(displayItem.Guid, displayItem);
            var currentTimeFrequency = Stopwatch.GetTimestamp();
            switch (displayItem)
            {
                case TextDisplayItem text:
                    LowUpdateFrequencyDisplayItemStringValues[text.Guid] = text.EvaluateText();
                    break;
                case ImageDisplayItem image:
                    // URL の場合は更新間隔でリクエストが発生できるよう初期値として現在時刻を代入
                    // FPSのチェック時に更新間隔の基準点がずれないよう GetOrAdd で代入
                    if (image.Type == ImageDisplayItem.ImageType.URL)
                    {
                        _ = LowUpdateFrequencyDisplayItemLongValues.GetOrAdd(image.Guid, currentTimeFrequency);
                    }
                    break;
            }
        }
        // 低頻度更新ディスプレイアイテムを削除するための共通メソッド
        private static void RemoveLowFrequencyDicByDisplayItem(DisplayItem displayItem)
        {
            _ = LowUpdateFrequencyDisplayItemIds.TryRemove(displayItem.Guid, out _);
            _ = LowUpdateFrequencyDisplayItemStringValues.TryRemove(displayItem.Guid, out _);
            _ = LowUpdateFrequencyDisplayItemLongValues.TryRemove(displayItem.Guid, out _);
        }


        // 日時フォーマット文字列から、指定された書式指定子（s または f）の最大連続桁数を取得
        static int GetFormatDigits(string format, char targetChar)
        {
            if (string.IsNullOrEmpty(format)) return 0;

            // 検索対象の文字パターンを作成
            string targetPattern;
            if (char.ToLower(targetChar) == 'f')
            {
                // ミリ秒の場合は大文字Fと小文字fの両方を対象にする
                targetPattern = "([fF]+)";
            }
            else
            {
                // 秒（s）やその他の文字の場合
                targetPattern = $"({char.ToLower(targetChar)}+)";
            }

            // 正規表現パターンの組み立て
            // 1. '[^']*'      : シングルクォーテーションで囲まれたリテラル
            // 2. "[^"]*"      : ダブルクォーテーションで囲まれたリテラル
            // 3. \\.          : バックスラッシュでエスケープされた1文字
            // 4. targetPattern: 目的の文字の連続
            string pattern = $@"'[^']*'|""[^""]*""|\\.|{targetPattern}";

            MatchCollection matches = Regex.Matches(format, pattern);

            int maxLength = 0;
            foreach (Match match in matches)
            {
                Group targetGroup = match.Groups[1];
                if (targetGroup.Success)
                {
                    if (targetGroup.Length > maxLength)
                    {
                        maxLength = targetGroup.Length;
                    }
                }
            }
            return maxLength;
        }
    }
}
