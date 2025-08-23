using FalconBMS.Launcher.Input;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FalconBMS.Launcher.Windows
{
    /// <summary>
    /// Interaction logic for OnlineSetupViewerWindow.xaml
    /// </summary>
    public partial class OnlineSetupViewerWindow : ITimerSink
    {
        private static readonly HttpClient _http = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        public OnlineSetupViewerWindow()
        {
            InitializeComponent();
            _ = RefreshAsync();
        }

        public static void ShowOnlineSetupViewerWindow()
        {
            OnlineSetupViewerWindow ownWindow = new OnlineSetupViewerWindow();
            Program.ShowDialogAndMakeActive(ownWindow);
        }

        public void HandleTimerTick()
        {
            return;
        }

        private async Task RefreshAsync()
        {
            try
            {
                SetStatus("Loading...");
                var url = "http://localhost:3000/api/xml_documents";

                _http.DefaultRequestHeaders.Accept.Clear();
                _http.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                var resp = await _http.GetAsync(url).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                var table = BuildDataTableFromJson(json);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (DocumentsGrid != null)
                        DocumentsGrid.ItemsSource = table?.DefaultView;
                    SetStatus(table == null ? "No data" : $"Loaded {table.Rows.Count} items");
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (DocumentsGrid != null)
                        DocumentsGrid.ItemsSource = null;
                    SetStatus($"Error: {ex.Message}");
                });
            }
        }

        private static DataTable BuildDataTableFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            JToken token;
            try
            {
                token = JToken.Parse(json);
            }
            catch
            {
                return null;
            }

            // Expect an array of objects; if single object, wrap it.
            JArray array = token as JArray ?? new JArray(token);
            if (array.Count == 0) return new DataTable();

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JObject obj in array.OfType<JObject>())
                foreach (var prop in obj.Properties())
                    columns.Add(prop.Name);

            var table = new DataTable("Documents");
            foreach (var col in columns)
                table.Columns.Add(col, typeof(string));

            foreach (JObject obj in array.OfType<JObject>())
            {
                var row = table.NewRow();
                foreach (var col in columns)
                {
                    if (obj.TryGetValue(col, StringComparison.OrdinalIgnoreCase, out JToken val))
                        row[col] = ToScalarString(val);
                }
                table.Rows.Add(row);
            }

            return table;
        }

        private static string ToScalarString(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return string.Empty;
            if (token is JValue v) return Convert.ToString(v.Value);
            return token.Type == JTokenType.Array || token.Type == JTokenType.Object
                ? token.ToString(Formatting.None)
                : token.ToString();
        }

        private void SetStatus(string message)
        {
            if (StatusText != null)
                StatusText.Text = message ?? string.Empty;
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }
    }

}
