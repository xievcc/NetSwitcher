using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace NetSwitcher
{
    /// <summary>程序级设置（与网络方案分开存放，避免互相干扰）。</summary>
    internal sealed class JsonSettings
    {
        /// <summary>是否把界面收进托盘（仅记录用户习惯，暂未用于自动启动）。</summary>
        public bool StartMinimized { get; set; }

        /// <summary>切换后是否弹出屏幕提示。</summary>
        public bool ShowToast { get; set; }

        /// <summary>是否显示悬浮快捷切换面板。</summary>
        public bool PanelVisible { get; set; }

        /// <summary>快捷面板位置（用于下次打开时还原）。</summary>
        public int PanelX { get; set; }
        public int PanelY { get; set; }

        public JsonSettings()
        {
            ShowToast = true;
            PanelVisible = false;
        }
    }

    /// <summary>设置的 JSON 持久化：与 profiles.json 同目录，文件名 settings.json。</summary>
    internal static class SettingsStore
    {
        private static string FilePath
        {
            get { return Path.Combine(ProfileStore.ProfileDirectory, "settings.json"); }
        }

        internal static JsonSettings Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new JsonSettings();
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return new JsonSettings();

                var serializer = new JavaScriptSerializer();
                JsonSettings s = serializer.Deserialize<JsonSettings>(json);
                return s ?? new JsonSettings();
            }
            catch
            {
                return new JsonSettings();
            }
        }

        internal static void Save(JsonSettings settings)
        {
            try
            {
                Directory.CreateDirectory(ProfileStore.ProfileDirectory);
                var serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(settings ?? new JsonSettings());
                File.WriteAllText(FilePath, json, new UTF8Encoding(false));
            }
            catch
            {
                // 设置存不下来不该影响主功能
            }
        }
    }
}
