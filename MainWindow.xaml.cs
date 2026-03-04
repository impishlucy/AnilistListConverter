using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using AniListNet;
using AniListNet.Objects;
using AniListNet.Parameters;
using FuzzySharp;

namespace AnilistListConverter;

public partial class MainWindow : Window
{
    // This is to get the First Instance back in Focus after Logging in.

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    // Global Vars

    private AniClient aniClient = new AniClient();
    private int ratelimit = 2100;

    // This allows to wait for the pipe to receive the token
    private TaskCompletionSource<string>? _authTcs;

    public MainWindow()
    {
        InitializeComponent();
        InitializeClient();

        // Start the server that waits for the token.
        StartNamedPipeServer();

        this.MouseDown += delegate (object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        };
    }

    private void InitializeClient()
    {
        EnsureProtocolIsRegistered();
        aniClient.RateChanged += OnRateChanged;
    }

    // This new Method allows the App to directly get the Token via a Callback.
    private void EnsureProtocolIsRegistered()
    {
        string customProtocol = "AnilistConverter";
        string applicationPath = Environment.ProcessPath;
        string registryPath = $@"Software\Classes\{customProtocol}";

        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(registryPath))
        {
            if (key != null)
            {
                using (RegistryKey commandKey = key.OpenSubKey(@"shell\open\command"))
                {
                    if (commandKey != null)
                    {
                        string currentCommand = commandKey.GetValue("") as string;
                        string expectedCommand = $"\"{applicationPath}\" \"%1\"";
                        if (currentCommand == expectedCommand) return;
                    }
                }
            }
        }
        RegisterProtocol(customProtocol, applicationPath);
    }

    // This registers the App in Registry, to allow to use AnilistConverter:// in a Browser
    private void RegisterProtocol(string protocol, string path)
    {
        using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{protocol}"))
        {
            key.SetValue("", "URL:AniList Auth Protocol");
            key.SetValue("URL Protocol", "");
            using (var commandKey = key.CreateSubKey(@"shell\open\command"))
            {
                commandKey.SetValue("", $"\"{path}\" \"%1\"");
            }
        }
    }

    // This puts the App into the "Background" to wait for the Token.
    private void StartNamedPipeServer()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream("AnilistAuthPipe", PipeDirection.In);
                    await server.WaitForConnectionAsync();

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string url = await reader.ReadLineAsync();

                    if (!string.IsNullOrEmpty(url))
                    {
                        FocusWindow();

                        if (_authTcs != null)
                        {
                            Dispatcher.Invoke(() => _authTcs.TrySetResult(url));
                        }
                    }
                }
                catch { /* Well fuck lol, idk. */ }
            }
        });
    }

    // Restore if minimized, then bring to front
    private void FocusWindow()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);

            this.Activate();
            this.Focus();
        });
    }

    private async void ApiButton_OnClick(object sender, RoutedEventArgs e)
    {
        LogBox.Text += "\nOpening AniList for authorization...";
        LogBox.ScrollToEnd();

        ApiButton.IsEnabled = false;
        _authTcs = new TaskCompletionSource<string>();

        OpenAuthWebsite();

        // Execution pauses here until the Pipe receives the token.
        string fullUrl = await _authTcs.Task;

        bool hasAPI = await HandleAPILogin(fullUrl);

        if (hasAPI)
        {
            Confirm.Content = "Click to Move Entries.";
            Confirm.IsEnabled = true;
            ToggleList.IsEnabled = true;
            ApiButton.Content = "Logged In";
            LogBox.Text += $"\nToken Accepted, Logged into Anilist.";
        }
        else
        {
            ApiButton.IsEnabled = true;
            ApiButton.Content = "Login Failed - Try Again";
            LogBox.Text += $"\nToken Rejected by API, try again.";
        }
    }

    private void OpenAuthWebsite()
    {
        string clientId = "21239";

        string authUrl = $"https://anilist.co/api/v2/oauth/authorize?client_id={clientId}&response_type=token";

        System.Diagnostics.Process.Start(new ProcessStartInfo
        {
            FileName = authUrl,
            UseShellExecute = true
        });
    }

    public async Task<bool> HandleAPILogin(string fullUrl)
    {
        string token = "";
        try
        {
            // The token should usually be in the URL fragment (#), if not uhh how ?
            if (fullUrl.Contains("#access_token="))
            {
                token = fullUrl.Split("#access_token=")[1].Split('&')[0];
            }
        }
        catch (Exception ex)
        {
            LogBox.Text += $"\nError parsing token: {ex.Message}";
            return false;
        }

        if (string.IsNullOrEmpty(token)) return false;

        bool success = await aniClient.TryAuthenticateAsync(token);
        return success;
    }

    private void ToggleList_OnClick(object sender, RoutedEventArgs e)
    {
        bool toggle = ToggleList.IsChecked.GetValueOrDefault();
        ToggleList.Content = toggle ? "Move Anime to Manga" : "Move Manga to Anime";
    }

    private void ConfirmSettings_OnClick(object sender, RoutedEventArgs e)
    {
        if (Confirm.IsEnabled)
        {
            bool toggle = ToggleList.IsChecked.Value;
            MoveLists(toggle);
        }
    }

    // Main function to move the lists, redone for proper Ratelimit handling.
    private async void MoveLists(bool direction)
    {
        try
        {
            MediaType originalType = direction ? MediaType.Anime : MediaType.Manga;
            MediaType newType = direction ? MediaType.Manga : MediaType.Anime;

            LogBox.Text += $"\nStarting move from {originalType} to {newType}...";
            LogBox.ScrollToEnd();

            Confirm.IsEnabled = false;
            ToggleList.IsEnabled = false;

            if (!aniClient.IsAuthenticated) return;

            var user = await aniClient.GetAuthenticatedUserAsync();
            if (user == null) return;

            int page = 1;
            List<MediaEntry> entriesToMove = new();
            MediaEntryCollection collection;

            // --- FETCHING ENTRIES ---
            do
            {
                await Task.Delay(ratelimit);
                collection = await aniClient.GetUserEntryCollectionAsync(user.Id, originalType, new AniPaginationOptions(page, 25));
                foreach (var list in collection.Lists)
                {
                    entriesToMove.AddRange(list.Entries.Where(x => x.Status == MediaEntryStatus.Planning));
                }
                page++;
            } while (collection.HasNextChunk);

            if (entriesToMove.Count == 0)
            {
                LogBox.Text += "\nNo Planning entries found.";
                return;
            }

            // --- PROCESSING ENTRIES ---
            Progress.Visibility = Visibility.Visible;
            int current = 0;

            foreach (var entry in entriesToMove)
            {
                current++;
                Progress.Value = (double)current / entriesToMove.Count * 100;
                Confirm.Content = $"Processing {current}/{entriesToMove.Count}";

                string nativeTitle = entry.Media.Title?.NativeTitle;
                if (string.IsNullOrEmpty(nativeTitle)) continue;

                LogBox.Text += $"\nProcessing {entry.Media.Title.EnglishTitle ?? nativeTitle}";
                LogBox.ScrollToEnd();

                // TRUE RETRY LOOP FOR INDIVIDUAL ENTRY
                bool success = false;
                while (!success)
                {
                    try
                    {
                        // 1. Search for target
                        await Task.Delay(ratelimit);
                        var searchResults = await aniClient.SearchMediaAsync(new SearchMediaFilter { Query = nativeTitle, Type = newType }, new AniPaginationOptions(1, 10));

                        int targetId = 0;
                        foreach (var result in searchResults.Data)
                        {
                            if (Fuzz.Ratio(result.Title.NativeTitle, nativeTitle) >= 85)
                            {
                                targetId = result.Id;
                                break;
                            }
                        }

                        if (targetId != 0)
                        {
                            // 2. Delete original and 3. Save new
                            await Task.Delay(ratelimit);
                            await aniClient.DeleteMediaEntryAsync(entry.Id);

                            await Task.Delay(ratelimit);
                            await aniClient.SaveMediaEntryAsync(targetId, new MediaEntryMutation { Status = MediaEntryStatus.Planning });
                        }

                        success = true; // Success, break the 'while' loop for this entry
                    }
                    catch (Exception ex) when (ex.Message.Contains("429"))
                    {
                        // The 'ratelimit' variable was just updated by OnRateChanged.
                        LogBox.Text += $"\n[!] 429 Hit. Waiting {ratelimit}ms before retry...";
                        LogBox.ScrollToEnd();

                        await Task.Delay(ratelimit);
                        // The 'while' loop continues to retry this exact entry.
                    }
                }
            }

            // SUCCESSFUL COMPLETION LOGIC
            Dispatcher.Invoke(() => {
                LogBox.Text += "\nProcess complete.";
                Confirm.Content = "Finished!";
            });
        }
        catch (Exception ex)
        {
            // OUTER ERROR HANDLING
            Dispatcher.Invoke(() => {
                LogBox.Text += $"\n[Error] Process failed: {ex.Message}";
                LogBox.ScrollToEnd();
            });
        }
        finally
        {
            // UI RESET LOGIC (Always executes)
            Dispatcher.Invoke(() => {
                Confirm.IsEnabled = true;
                ToggleList.IsEnabled = true;
                Progress.Visibility = Visibility.Hidden;
            });
        }
    }

    private void OnRateChanged(object? sender, AniRateEventArgs e)
    {
        // The definition confirms RetryAfter is an int? of seconds.
        if (e.RetryAfter.HasValue && e.RetryAfter.Value > 0)
        {
            // Convert seconds to milliseconds and add a 1.5s buffer.
            ratelimit = (e.RetryAfter.Value * 1000) + 1500;

            Dispatcher.Invoke(() =>
            {
                LogBox.Text += $"\n[!] Rate Limit hit. Pausing for {e.RetryAfter.Value}s.";
                LogBox.ScrollToEnd();
            });
        }
        else
        {
            // Dynamic pacing: if we have more than 10 requests left, stay fast (750ms).
            // Otherwise, slow down to 3s to prevent hitting the hard block.
            ratelimit = e.RateRemaining < 10 ? 3000 : 750;
        }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Environment.Exit(0);
}