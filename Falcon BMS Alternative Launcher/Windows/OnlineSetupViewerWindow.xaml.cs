using FalconBMS.Launcher.Input;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
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
    private HttpClient _http => ApiSession.Instance.Client;

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
                var url = "/api/xml_documents";

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

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new LoginWindow { Owner = this };
            var result = dlg.ShowDialog();
            if (result == true)
            {
                await RefreshAsync();
            }
        }

        private void DocumentsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DownloadButton == null) return;
            DownloadButton.IsEnabled = DocumentsGrid?.SelectedItem != null;
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selected = DocumentsGrid?.SelectedItem as DataRowView;
                if (selected == null)
                {
                    SetStatus("No selection");
                    return;
                }

                // Try common identifier property names
                string id = GetValue(selected, new[] { "id", "Id", "_id" });
                if (string.IsNullOrWhiteSpace(id))
                {
                    SetStatus("Could not determine document id");
                    return;
                }

                await DownloadDocumentAsync(id);
            }
            catch (Exception ex)
            {
                SetStatus($"Download error: {ex.Message}");
            }
        }

        private static string GetValue(DataRowView row, IEnumerable<string> candidateColumns)
        {
            foreach (var name in candidateColumns)
            {
                if (row.DataView.Table.Columns.Contains(name))
                {
                    var v = row[name]?.ToString();
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }
            return null;
        }

        private async Task DownloadDocumentAsync(string id)
        {
            SetStatus("Downloading...");
            var url = $"/xml_documents/{Uri.EscapeDataString(id)}/download";

            // Accept redirects and try to honor filename via Content-Disposition
            using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiSession.Instance.BaseUri, url)))
            {
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

                using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();

                    // Determine filename
                    string fileName = TryGetFileName(response.Content.Headers) ?? $"document_{id}.xml";

                    // Choose download path: user Downloads folder
                    var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    var defaultDir = System.IO.Path.Combine(downloads, "Downloads");
                    var targetDir = System.IO.Directory.Exists(defaultDir) ? defaultDir : downloads;
                    var filePath = System.IO.Path.Combine(targetDir, fileName);

                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var fs = System.IO.File.Create(filePath))
                    {
                        await stream.CopyToAsync(fs).ConfigureAwait(false);
                    }

                    await Dispatcher.InvokeAsync(() => SetStatus($"Saved: {filePath}"));
                }
            }
        }

        private static string TryGetFileName(HttpContentHeaders headers)
        {
            var dispo = headers?.ContentDisposition;
            if (dispo != null && !string.IsNullOrEmpty(dispo.FileNameStar))
                return dispo.FileNameStar.Trim('"');
            if (dispo != null && !string.IsNullOrEmpty(dispo.FileName))
                return dispo.FileName.Trim('"');
            return null;
        }
    }

}
