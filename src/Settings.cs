using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace RetroSync
{
    // Settings for the PS2 memory card part (same values PS2CardSync used to keep in settings.ini).
    public class Ps2Settings
    {
        public bool IncludeInSyncSave = true;
        public string LocalDir = "";
        public string RemoteDir = "/data/PCSX2";
        public string CardName = "my memory card.ps2";
        public int SkewMin = 2;
        public string MymcPath = "";
    }

    // One prefix mapping used to translate absolute paths (playlists) between PC and PS5.
    // Tokens: {PC_ROOT} = PcRetroArchRoot, {PS5_APP} = Ps5AppRoot.
    public class PathMapping
    {
        public string Pc;
        public string Ps5;
    }

    // Which retroarch.cfg / override keys are platform specific and must never be copied.
    // Patterns are globs ('*' and '?'), case-insensitive.
    public class KeyFilterSettings
    {
        public List<string> PlatformKeys = new List<string>();
        public List<string> AlwaysShareKeys = new List<string>();
        public bool SkipPathLikeValues = true;           // values like ":\x", ":/x", "/app0/x", "C:\x" are never copied
        public bool TreatInputTextBindsAsPlatform = true; // input_* keys with non numeric/bool values (keyboard binds) are platform specific
        public bool AddMissingKeys = false;              // keys present on one side only are never added to the other side
    }

    // Remote power-on (Remote Play wake, see RemoteWake.cs). New in 2.0.
    public class WakeSettings
    {
        public string Credential = "";      // decimal "user-credential" (derived from RegistKey, or typed in)
        public string RegistKey = "";       // PS5-RegistKey (hex) from the pairing
        public string RpKey = "";           // RP-Key (hex) from the pairing, kept for a future "wake and sign in"
        public string AccountId = "";       // PSN account id (base64) sent by the pairing helper; informational, kept so older files still load
        public string Mac = "";
        public string Nickname = "";
        public string PairedOn = "";
        public int WaitSeconds = 180;       // how long "Wake and sync" waits for the FTP port
        public int SettleSeconds = 8;       // extra wait once FTP answers (payload start-up)
        public string AfterWake = "all";    // saves | config | all | none
        public bool UseDiscovery = true;    // ask the PS5 for its status (UDP 9302) for the status chips

        public ulong CredentialValue()
        {
            ulong v;
            if (!string.IsNullOrEmpty(Credential) && ulong.TryParse(Credential.Trim(), out v) && v > 0) return v;
            if (!string.IsNullOrEmpty(RegistKey)) { try { return RemotePlay.CredentialFromRegistKey(RegistKey); } catch { } }
            return 0;
        }

        public bool IsConfigured() { return CredentialValue() > 0; }
    }

    // GUI preferences. New in 2.0.
    public class UiSettings
    {
        public int StatusPollSeconds = 20;  // 0 = never poll the PS5 in the background
        public bool HideIdentical = true;
        public int EmulatorBackupKeep = 10; // versions kept per emulator folder backup
        public string Language = "en";      // "en" | "it" — English is the default for new installs
    }

    public class AppSettings
    {
        public const int CurrentVersion = 3;
        public int SettingsVersion;              // 0 = file written before v2 (gets the new defaults merged in)

        // ---- RetroArch locations
        public string PcRetroArchRoot = "";
        public string FtpHost = "";
        public int FtpPort = 2121;
        public string FtpUser = "anonymous";
        public string FtpPassword = "";
        public int FtpTimeoutSeconds = 20;
        public string Ps5RetroArchRoot = "/data/homebrew/RetroArch";  // FTP path of the RetroArch app folder (set to your title id folder)
        public string Ps5AppRoot = "/app0";                           // same folder as seen by RetroArch on the PS5
        public string PcMainConfig = "retroarch.cfg";                 // relative to PcRetroArchRoot
        public string Ps5MainConfig = "config/retroarch.cfg";         // relative to Ps5RetroArchRoot

        // ---- PS5 full mirror (one-way backup PS5 -> PC, never writes to the PS5)
        public string MirrorRemoteRoot = "/data/homebrew/RetroArch";
        public string MirrorLocalRoot = "";
        public List<string> MirrorExcludes = new List<string> { "content/**", "*.retrosync.part" };
        public bool MirrorMoveDeleted = false;     // move files removed on the PS5 to _deleted\<timestamp>
        public bool MirrorKeepPrevious = true;     // keep the old copy of a changed files in _previous\<timestamp>
        public bool MirrorMeasureExcluded = true;  // list excluded folders too, only to report their size

        // ---- Backups / time handling
        public string BackupRoot = "";
        public int BackupKeep = 20;
        public int TimeToleranceSeconds = 120;
        public bool Ps5ListTimesAreUtc = true;   // only used when the server has no MLSD (MLSD is always UTC)
        public int Ps5ClockOffsetMinutes = 0;    // added to every PS5 time, for a PS5 clock that is off

        // ---- Saves / states
        public string PcSavesDir = "saves";
        public string Ps5SavesDir = "savefiles";
        public string PcStatesDir = "states";
        public string Ps5StatesDir = "savestates";
        public string PcSaveLayout = "auto";     // auto (read sort_savefiles_enable / sort_savestates_enable) | flat | core
        public string Ps5SaveLayout = "auto";
        public List<string> SaveExcludes = new List<string>
        {
            "steam_autocloud.vdf", "desktop.ini", "Thumbs.db", "*.tmp", "*.retrosync.tmp",
            // deliberately deleted on the PS5 on 18/10/2026
            "Super Mario 64*.srm"
        };
        public Dictionary<string, string> GameCoreOverrides = new Dictionary<string, string>();
        public Dictionary<string, string> SubfolderCoreMap = new Dictionary<string, string> { { "fbneo", "FinalBurn Neo" }, { "opera", "Opera" } };

        // ---- Configuration
        public string PcConfigDir = "config";
        public string Ps5ConfigDir = "config";
        public List<string> ConfigExtensions = new List<string> { ".opt", ".cfg", ".slangp", ".rmp" };
        public List<string> ConfigExcludes = new List<string>
        {
            "retroarch.cfg", "frontend.cfg", "framegen.cfg", "webui-metadata/**", "record/**", "steam_autocloud.vdf", "* - Copia.*",
            // deliberately deleted on the PS5 on 18/10/2026 (DA_ELIMINARE.txt)
            "Mupen64Plus-Next/Super Mario 64*", "remaps/Mupen64Plus-Next/Super Mario 64*",
            "Mupen64Plus-Next/007 - GoldenEye*", "remaps/Mupen64Plus-Next/007 - GoldenEye*",
            "Flycast/GigaWing 2.cfg", "Flycast/Sega - Dreamcast.cfg",
            // PS1 per-game files and remaps (no PS1 games on the PS5)
            "Beetle PSX HW/Castlevania*", "Beetle PSX HW/Crash*", "Beetle PSX HW/SCES-*", "Beetle PSX HW/Spyro*",
            "Beetle PSX HW/Syphon Filter*", "Beetle PSX HW/WipEout*", "Beetle PSX HW/Metal Gear*",
            "remaps/Beetle PSX HW/Castlevania*", "remaps/Beetle PSX HW/Syphon Filter*", "remaps/Beetle PSX HW/WipEout*",
            // cores that exist only on the PC (no .so in the PS5 port)
            "Beetle PSX/**", "remaps/Beetle PSX/**", "PCSX-ReARMed/**", "remaps/PCSX-ReARMed/**",
            "SwanStation/**", "remaps/SwanStation/**", "melonDS/**", "remaps/melonDS/**",
            "O2EM/**", "remaps/O2EM/**", "YabaSanshiro/**", "remaps/YabaSanshiro/**"
        };
        public bool MergeKeyValueFiles = true;   // .cfg/.opt are merged key by key instead of copied whole
        public List<string> ShaderDirs = new List<string> { "shaders/shaders_slang/CRT preferiti" };
        public List<string> ShaderExcludes = new List<string> { "retroarch.slangp", "steam_autocloud.vdf" };
        public string PlaylistDir = "playlists";
        public List<string> PlaylistExcludes = new List<string> { "builtin/content_history.lpl", "builtin/content_image_history.lpl", "builtin/content_music_history.lpl", "builtin/content_video_history.lpl", "logs/**", "steam_autocloud.vdf" };
        public bool PlaylistDropMissingContent = true;
        public List<PathMapping> PathMappings = new List<PathMapping>
        {
            new PathMapping { Pc = @"{PC_ROOT}", Ps5 = "/app0/content" },
            new PathMapping { Pc = "{PC_ROOT}", Ps5 = "{PS5_APP}" }
        };
        public string PcCoreExtension = ".dll";
        public string Ps5CoreExtension = ".so";

        public KeyFilterSettings MainConfigFilter = DefaultMainFilter();
        // Extra keys that are platform specific only in the global retroarch.cfg; per-core / per-game
        // overrides (config\<core>\*.cfg) still share them (e.g. rewind_enable set per core).
        public List<string> MainConfigOnlyPlatformKeys = new List<string> { "rewind_*", "state_slot", "savestate_thumbnail_enable", "fps_show", "fps_update_interval" };
        public List<string> CoreOptionPlatformKeys = new List<string>
        {
            // renderer / plugin availability and GPU-bound resolution differ between PC and PS5
            "mupen64plus-rdp-plugin", "mupen64plus-rsp-plugin", "mupen64plus-ThreadedRenderer",
            "mupen64plus-43screensize", "mupen64plus-169screensize", "mupen64plus-parallel-rdp-upscaling",
            "beetle_psx_hw_renderer", "beetle_psx_hw_internal_resolution",
            "flycast_internal_resolution", "reicast_internal_resolution", "flycast_threaded_rendering", "reicast_threaded_rendering"
        };

        public Ps2Settings Ps2 = new Ps2Settings();
        public WakeSettings Wake = new WakeSettings();
        public UiSettings Ui = new UiSettings();

        public static KeyFilterSettings DefaultMainFilter()
        {
            var f = new KeyFilterSettings();
            f.PlatformKeys = new List<string>
            {
                // drivers
                "*_driver",
                // paths / directories
                "*_directory", "*_dir", "*_path", "*_paths", "video_shader", "video_filter", "audio_dsp_plugin", "menu_wallpaper",
                "xmb_font", "ozone_*font*", "input_overlay", "input_osk_overlay", "bundle_assets_*", "*_in_content_dir",
                "sort_savefiles*", "sort_savestates*", "sort_screenshots*", "core_updater_buildbot_*", "libretro_*",
                // input binds, devices, hotkeys
                "input_player*", "input_*_btn", "input_*_axis", "input_*_mbtn", "input_device_p*", "input_joypad_*",
                "input_autodetect_*", "input_*_gamepad_combo", "input_max_users", "input_hotkey_*", "input_overlay*",
                "input_keyboard_*", "input_mouse_*", "input_sensors_*", "input_auto_mouse_grab", "input_auto_game_focus",
                // video output
                "video_refresh_rate*", "video_fullscreen*", "video_windowed_*", "video_window_*", "video_monitor_index",
                "video_gpu_index", "video_swap_interval", "video_hard_sync*", "video_max_swapchain_images", "video_waitable_swapchains",
                "video_adaptive_vsync", "video_vsync", "video_threaded", "video_hdr_*", "video_shared_context", "video_frame_delay*",
                "video_black_frame_insertion", "video_shader_subframes", "video_autoswitch_*", "video_ctx_scaling", "video_scale",
                "video_force_srgb_disable", "video_allow_rotate", "vrr_runloop_enable", "crt_switch_*", "*framegen*",
                "ui_*", "menu_mouse_enable", "menu_pointer_enable", "pause_nonactive", "suspend_screensaver_enable",
                "menu_battery_level_enable", "video_record_*", "video_stream_*",
                // audio
                "audio_device", "audio_latency", "audio_out_rate", "audio_block_frames", "audio_enable_menu*", "audio_wasapi_*",
                "audio_resampler*", "microphone_*", "midi_*",
                // logging / misc platform
                "log_*", "frontend_log_level", "libretro_log_level", "cache_directory", "core_assets_directory",
                "cloud_sync_*", "steam_*", "config_save_on_exit", "kiosk_mode_*",
                // menu / theme / assets / OSD (cosmetic, per screen)
                "menu_*", "quick_menu_*", "settings_show_*", "content_show_*", "playlist_show_*", "playlist_sublabel_*",
                "xmb_*", "ozone_*", "rgui_*", "materialui_*", "glui_*", "assets_*", "all_users_control_menu",
                "builtin_mediaplayer_enable", "builtin_imageviewer_enable", "notification_show_*", "video_msg_*", "video_font_*",
                "framecount_show", "memory_show", "statistics_show", "input_bind_*",
                // netplay identity / network
                "netplay_*",
                // secrets never travel
                "*_password", "*_token", "*_stream_key", "cheevos_username", "webdav_*"
            };
            f.AlwaysShareKeys = new List<string> { "input_player*_analog_dpad_mode" };
            return f;
        }
    }

    static class SettingsStore
    {
        // ExeDir = folder of RetroSync.exe. DataDir = writable settings/state location.
        // Portable (default): DataDir == ExeDir. Installed: %APPDATA%\RetroSync when
        // installed.flag exists next to the exe, or the exe folder is not writable.
        // Existing portable installs keep settings next to the exe if RetroSync.settings.json is already there.
        public static readonly string ExeDir;
        public static readonly string DataDir;
        public static readonly string AppDir;          // = DataDir (storage root; kept for callers)
        public static readonly string SettingsPath;
        public static readonly string StatePath;
        public static readonly bool IsPortable;
        public const string InstalledFlagName = "installed.flag";

        // Test hooks (null in production)
#pragma warning disable 649
        public static string ExeDirOverride;
        public static string AppDataOverride;
        public static Func<string, bool> WritableCheckOverride;
#pragma warning restore 649

        static SettingsStore()
        {
            ExeDir = (ExeDirOverride ?? AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\', '/');
            string appData = AppDataOverride;
            if (string.IsNullOrEmpty(appData))
            {
                try { appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RetroSync"); }
                catch { appData = Path.Combine(ExeDir, "data"); }
            }
            DataDir = ResolveDataDir(ExeDir, appData);
            IsPortable = string.Equals(Path.GetFullPath(DataDir), Path.GetFullPath(ExeDir), StringComparison.OrdinalIgnoreCase);
            AppDir = DataDir;
            SettingsPath = Path.Combine(DataDir, "RetroSync.settings.json");
            StatePath = Path.Combine(DataDir, "RetroSync.state.json");
            try { Directory.CreateDirectory(DataDir); } catch { }
        }

        /// <summary>Pick portable (exeDir) vs installed (appData). Pure helper for tests.</summary>
        public static string ResolveDataDir(string exeDir, string appData)
        {
            if (string.IsNullOrEmpty(exeDir)) exeDir = ".";
            if (string.IsNullOrEmpty(appData)) appData = Path.Combine(exeDir, "data");
            bool flag = File.Exists(Path.Combine(exeDir, InstalledFlagName));
            bool settingsBeside = File.Exists(Path.Combine(exeDir, "RetroSync.settings.json"));
            bool writable = DirIsWritable(exeDir);
            // 1) Installer marker → app data
            if (flag) return appData;
            // 2) Existing portable settings beside exe → keep them (backwards compatible)
            if (settingsBeside) return exeDir;
            // 3) Writable exe folder → portable
            if (writable) return exeDir;
            // 4) Fallback → app data
            return appData;
        }

        public static bool DirIsWritable(string dir)
        {
            if (WritableCheckOverride != null) return WritableCheckOverride(dir);
            try
            {
                if (!Directory.Exists(dir)) return false;
                string probe = Path.Combine(dir, ".retrosync-write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }


        static JavaScriptSerializer Ser() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 }; }

        public static AppSettings Load(out string error)
        {
            error = null;
            if (!File.Exists(SettingsPath))
            {
                var d = new AppSettings { SettingsVersion = AppSettings.CurrentVersion };
                try { Save(d); } catch (Exception e) { error = e.Message; }
                return d;
            }
            try
            {
                var s = Ser().Deserialize<AppSettings>(File.ReadAllText(SettingsPath, Encoding.UTF8));
                if (s == null) s = new AppSettings();
                Normalize(s);
                return s;
            }
            catch (Exception e)
            {
                error = "Settings unreadable (" + e.Message + "): using defaults.";
                return new AppSettings();
            }
        }

        static void Union(List<string> target, List<string> add)
        {
            foreach (var x in add) if (!target.Contains(x)) target.Add(x);
        }

        static void Normalize(AppSettings s)
        {
            var d = new AppSettings();
            if (s.Ps2 == null) s.Ps2 = d.Ps2;
            if (s.Wake == null) s.Wake = new WakeSettings();
            if (s.Ui == null) s.Ui = new UiSettings();
            if (s.MainConfigFilter == null) s.MainConfigFilter = d.MainConfigFilter;
            if (s.MainConfigFilter.PlatformKeys == null) s.MainConfigFilter.PlatformKeys = d.MainConfigFilter.PlatformKeys;
            if (s.MainConfigFilter.AlwaysShareKeys == null) s.MainConfigFilter.AlwaysShareKeys = new List<string>();
            if (s.SaveExcludes == null) s.SaveExcludes = d.SaveExcludes;
            if (s.GameCoreOverrides == null) s.GameCoreOverrides = new Dictionary<string, string>();
            if (s.SubfolderCoreMap == null) s.SubfolderCoreMap = d.SubfolderCoreMap;
            if (s.ConfigExtensions == null) s.ConfigExtensions = d.ConfigExtensions;
            if (s.ConfigExcludes == null) s.ConfigExcludes = d.ConfigExcludes;
            if (s.ShaderDirs == null) s.ShaderDirs = d.ShaderDirs;
            if (s.ShaderExcludes == null) s.ShaderExcludes = d.ShaderExcludes;
            if (s.PlaylistExcludes == null) s.PlaylistExcludes = d.PlaylistExcludes;
            if (s.PathMappings == null) s.PathMappings = d.PathMappings;
            if (s.CoreOptionPlatformKeys == null) s.CoreOptionPlatformKeys = d.CoreOptionPlatformKeys;
            if (s.MainConfigOnlyPlatformKeys == null) s.MainConfigOnlyPlatformKeys = d.MainConfigOnlyPlatformKeys;
            if (s.MirrorExcludes == null) s.MirrorExcludes = d.MirrorExcludes;
            if (string.IsNullOrEmpty(s.MirrorRemoteRoot)) s.MirrorRemoteRoot = d.MirrorRemoteRoot;
            if (string.IsNullOrEmpty(s.MirrorLocalRoot)) s.MirrorLocalRoot = d.MirrorLocalRoot;
            if (s.SettingsVersion < AppSettings.CurrentVersion)
            {
                // merge the new default entries into lists written by an older version (user entries are kept)
                Union(s.MainConfigFilter.PlatformKeys, d.MainConfigFilter.PlatformKeys);
                Union(s.ConfigExcludes, d.ConfigExcludes);
                Union(s.SaveExcludes, d.SaveExcludes);
                Union(s.CoreOptionPlatformKeys, d.CoreOptionPlatformKeys);
                Union(s.MainConfigOnlyPlatformKeys, d.MainConfigOnlyPlatformKeys);
                s.SettingsVersion = AppSettings.CurrentVersion;
                try { Save(s); } catch { }
            }
            if (s.BackupKeep < 1) s.BackupKeep = 1;
        }

        public static void Save(AppSettings s)
        {
            string json = JsonPretty.Format(Ser().Serialize(s));
            string tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
            File.Move(tmp, SettingsPath);
        }

        public static T LoadJson<T>(string path) where T : class, new()
        {
            if (!File.Exists(path)) return new T();
            try { return Ser().Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)) ?? new T(); }
            catch { return new T(); }
        }

        public static void SaveJson(string path, object o)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonPretty.Format(Ser().Serialize(o)), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }

    // Re-indents compact JSON (JavaScriptSerializer output) so the settings file is human editable.
    static class JsonPretty
    {
        public static string Format(string json)
        {
            var sb = new StringBuilder();
            int ind = 0; bool inStr = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length) { sb.Append(json[++i]); continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inStr = true; sb.Append(c); break;
                    case '{':
                    case '[':
                        // keep empty containers on one line
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(c).Append(json[i + 1]); i++; break; }
                        sb.Append(c); ind++; NewLine(sb, ind); break;
                    case '}':
                    case ']': ind--; NewLine(sb, ind); sb.Append(c); break;
                    case ',': sb.Append(c); NewLine(sb, ind); break;
                    case ':': sb.Append(": "); break;
                    default: if (!char.IsWhiteSpace(c)) sb.Append(c); break;
                }
            }
            return sb.ToString() + "\r\n";
        }

        static void NewLine(StringBuilder sb, int ind) { sb.Append("\r\n").Append(' ', Math.Max(0, ind) * 2); }
    }
}
