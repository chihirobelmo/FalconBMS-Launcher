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
using Microsoft.Win32;
using System.IO;

namespace FalconBMS.Launcher.Windows
{
    /// <summary>
    /// Interaction logic for OnlineSetupViewerWindow.xaml
    /// </summary>
    public partial class OnlineSetupViewerWindow : ITimerSink
    {
    private HttpClient _http => ApiSession.Instance.Client;
        // Columns to hide from the auto-generated DataGrid
        private readonly HashSet<string> _hiddenColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id", "description"
        };

        public OnlineSetupViewerWindow()
        {
            InitializeComponent();
            if (DocumentsGrid != null)
                DocumentsGrid.AutoGeneratingColumn += DocumentsGrid_AutoGeneratingColumn;
            _ = RefreshAsync();
            UpdateAuthUi();
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

                using (var req = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiSession.Instance.BaseUri, url)))
                {
                    req.Headers.Accept.Clear();
                    req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                    // List API is public; use PublicClient (no Authorization header)
                    var resp = await ApiSession.Instance.PublicClient.SendAsync(req).ConfigureAwait(false);
                    if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            DocumentsGrid.ItemsSource = null;
                            SetStatus("ログインしてください。");
                            UpdateAuthUi();
                        });
                        return;
                    }
                    resp.EnsureSuccessStatusCode();
                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    var table = BuildDataTableFromJson(json);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (DocumentsGrid != null)
                        {
                            DocumentsGrid.ItemsSource = table?.DefaultView;
                            ApplyHiddenColumns();
                        }
                        SetStatus(table == null ? "No data" : $"Loaded {table.Rows.Count} items");
                    });
                }
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

        // Hide unwanted columns when auto-generating
        private void DocumentsGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            if (_hiddenColumns.Contains(e.PropertyName))
            {
                e.Cancel = true;
                return;
            }
        }

        // Ensure hidden columns are collapsed after binding or re-binding
        private void ApplyHiddenColumns()
        {
            if (DocumentsGrid == null) return;
            foreach (var col in DocumentsGrid.Columns)
            {
                var header = col.Header?.ToString() ?? string.Empty;
                col.Visibility = _hiddenColumns.Contains(header) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private async void InlineLoginButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                InlineLoginButton.IsEnabled = false;
                SetStatus("Signing in...");
                var ok = await ApiSession.Instance.LoginAsync(LoginEmailBox.Text, LoginPasswordBox.Password, LoginRememberBox.IsChecked == true);
                if (ok)
                {
                    SetStatus("Login success");
                    await RefreshAsync();
                    UpdateAuthUi();
                }
                else
                {
                    SetStatus("Login failed");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Login error: {ex.Message}");
            }
            finally
            {
                InlineLoginButton.IsEnabled = true;
            }
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetStatus("Logging out...");
                var ok = await ApiSession.Instance.LogoutAsync();
                DocumentsGrid.ItemsSource = null;
                SetStatus(ok ? "Logged out" : "Logout failed");
                UpdateAuthUi();
            }
            catch (Exception ex)
            {
                SetStatus($"Logout error: {ex.Message}");
            }
        }

        private void UpdateAuthUi()
        {
            try
            {
                // With Bearer tokens, rely on ApiSession state
                bool loggedIn = ApiSession.Instance.IsLoggedIn;
                LoginPanel.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
                LogoutButton.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
                if (UploadPanel != null)
                    UploadPanel.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
            }
            catch
            {
                // Fallback: show login controls if uncertain
                LoginPanel.Visibility = Visibility.Visible;
                LogoutButton.Visibility = Visibility.Collapsed;
                if (UploadPanel != null)
                    UploadPanel.Visibility = Visibility.Collapsed;
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
            // Use API endpoint; Authorization header is set globally when logged in
            var url = $"/api/xml_documents/{Uri.EscapeDataString(id)}/download";

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

        private async void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ApiSession.Instance.IsLoggedIn)
            {
                SetStatus("アップロードにはログインが必要です。");
                UpdateAuthUi();
                return;
            }

            var title = UploadTitleBox?.Text?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                SetStatus("タイトルを入力してください。");
                UploadTitleBox?.Focus();
                return;
            }

            // Ask user to choose an XML file
            var ofd = new OpenFileDialog
            {
                Title = "アップロードするXMLファイルを選択",
                Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            string filePath;
            if (ofd.ShowDialog(this) == true)
            {
                filePath = ofd.FileName;
            }
            else
            {
                SetStatus("アップロードをキャンセルしました。");
                return;
            }

            if (!File.Exists(filePath))
            {
                SetStatus("ファイルが見つかりません。");
                return;
            }

            UploadButton.IsEnabled = false;
            SetStatus("アップロード中...");

            try
            {
                using (var form = new MultipartFormDataContent())
                {
                    // Basic fields
                    form.Add(new StringContent(title, Encoding.UTF8), "xml_document[title]");
                    // Optional description (server側で任意なら空でOK)
                    form.Add(new StringContent("Uploaded via Launcher", Encoding.UTF8), "xml_document[description]");

                    // File content
                    using (var fs = File.OpenRead(filePath))
                    {
                        var fileContent = new StreamContent(fs);
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/xml");
                        form.Add(fileContent, "xml_document[xml_file]", System.IO.Path.GetFileName(filePath));

                        using (var req = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiSession.Instance.BaseUri, "/api/xml_documents")))
                        {
                            req.Headers.Accept.Clear();
                            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                            req.Content = form;

                            using (var resp = await ApiSession.Instance.Client.SendAsync(req).ConfigureAwait(false))
                            {
                                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                                {
                                    await Dispatcher.InvokeAsync(() =>
                                    {
                                        SetStatus("認証エラー: 再度ログインしてください。");
                                        UpdateAuthUi();
                                    });
                                    return;
                                }

                                if (!resp.IsSuccessStatusCode)
                                {
                                    string body = null;
                                    try { body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }
                                    throw new Exception($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} {body}");
                                }

                                await Dispatcher.InvokeAsync(async () =>
                                {
                                    SetStatus("アップロード完了");
                                    UploadTitleBox.Text = string.Empty;
                                    // Refresh list (public endpoint)
                                    await RefreshAsync();
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => SetStatus($"アップロード失敗: {ex.Message}"));
            }
            finally
            {
                try
                {
                    await Dispatcher.InvokeAsync(() => UploadButton.IsEnabled = true);
                }
                catch
                {
                    // As a fallback, ignore if window is closing/disposed
                }
            }
        }
    }

}
