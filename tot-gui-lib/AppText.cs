using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tot_gui_lib
{
    public class AppText
    {
        protected Dictionary<string, string> _texts = new Dictionary<string, string>();

        private AppText()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else
                throw new InvalidOperationException("Can't start two instance of AppText");
        }

        public event EventHandler? TextSetChanged;

        public static AppText Instance { get; private set; } = new AppText();

        public static string Get(string key, params object[] args)
        {
            if (Instance._texts.TryGetValue(key, out var text)) return string.Format(text, args);
            return $"<INVALID_{key}>";
        }

        public static void Load(string embededPath)
        {
            var node = JsonSerializer.Deserialize<JsonNode>(tot_lib.Utils.GetEmbeddedTextFile(embededPath));
            if (node == null) return;

            Instance._texts.Clear();
            foreach (var n in node.AsObject())
                Instance._texts.Add(n.Key, n.Value?.GetValue<string>() ?? $"<INVALID_{n.Key}>");
            Instance.TextSetChanged?.Invoke(Instance, EventArgs.Empty);
        }
    }
}