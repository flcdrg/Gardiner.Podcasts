//*********************************************************
//
// Copyright (c) Microsoft. All rights reserved.
//
//*********************************************************

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Windows.Networking.BackgroundTransfer;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Windows.Web;
using Windows.Web.Syndication;

using Gardiner.Podcasts.Annotations;

namespace Gardiner.Podcasts
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class S1_Download : Page, IDisposable
    {
        // A pointer back to the main page.  This is needed if you want to call methods in MainPage such
        // as NotifyUser()
        private MainPage rootPage = MainPage.Current;

        private List<DownloadOperation> _activeDownloads;
        private CancellationTokenSource _cts;
        private DownloadViewModel _viewModel;

        public S1_Download()
        {
            _cts = new CancellationTokenSource();
            _viewModel = new DownloadViewModel();
            DataContext = _viewModel;
            this.InitializeComponent();
        }

        public void Dispose()
        {
            if (_cts != null)
            {
                _cts.Dispose();
                _cts = null;
            }

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Invoked when this page is about to be displayed in a Frame.
        /// </summary>
        /// <param name="e">Event data that describes how this page was reached.  The Parameter
        /// property is typically used to configure the page.</param>
        protected async override void OnNavigatedTo(NavigationEventArgs e)
        {
            // An application must enumerate downloads when it gets started to prevent stale downloads/uploads.
            // Typically this can be done in the App class by overriding OnLaunched() and checking for
            // "args.Kind == ActivationKind.Launch" to detect an actual app launch.
            // We do it here in the sample to keep the sample code consolidated.
            await DiscoverActiveDownloadsAsync();
        }


        // Enumerate the downloads that were going on in the background while the app was closed.
        private async Task DiscoverActiveDownloadsAsync()
        {
            _activeDownloads = new List<DownloadOperation>();

            IReadOnlyList<DownloadOperation> downloads = null;
            try
            {
                downloads = await BackgroundDownloader.GetCurrentDownloadsAsync();
            }
            catch (Exception ex)
            {
                if (!IsExceptionHandled("Discovery error", ex))
                {
                    throw;
                }
                return;
            }

            Log("Loading background downloads: " + downloads.Count);

            if (downloads.Count > 0)
            {
                List<Task> tasks = new List<Task>();
                foreach (DownloadOperation download in downloads)
                {
                    Log(String.Format(CultureInfo.CurrentCulture,
                        "Discovered background download: {0}, Status: {1}", download.Guid,
                        download.Progress.Status));

                    // Attach progress and completion handlers.
                    tasks.Add(HandleDownloadAsync(download, false));
                }

                // Don't await HandleDownloadAsync() in the foreach loop since we would attach to the second
                // download only when the first one completed; attach to the third download when the second one
                // completes etc. We want to attach to all downloads immediately.
                // If there are actions that need to be taken once downloads complete, await tasks here, outside
                // the loop.
                await Task.WhenAll(tasks);
            }
        }

        private async void StartDownload(BackgroundTransferPriority priority, bool requestUnconstrainedDownload)
        {
            // Validating the URI is required since it was received from an untrusted source (user input).
            // The URI is validated by calling Uri.TryCreate() that will return 'false' for strings that are not valid URIs.
            // Note that when enabling the text box users may provide URIs to machines on the intrAnet that require
            // the "Home or Work Networking" capability.
            Uri source;
            if (!Uri.TryCreate(serverAddressField.Text.Trim(), UriKind.Absolute, out source))
            {
                rootPage.NotifyUser("Invalid URI.", NotifyType.ErrorMessage);
                return;
            }

            string destination = fileNameField.Text.Trim();

            if (string.IsNullOrWhiteSpace(destination))
            {
                rootPage.NotifyUser("A local file name is required.", NotifyType.ErrorMessage);
                return;
            }

            StorageFile destinationFile;
            try
            {
                destinationFile = await KnownFolders.MusicLibrary.CreateFileAsync(
                    destination, CreationCollisionOption.GenerateUniqueName);
            }
            catch (FileNotFoundException ex)
            {
                rootPage.NotifyUser("Error while creating file: " + ex.Message, NotifyType.ErrorMessage);
                return;
            }

            BackgroundDownloader downloader = new BackgroundDownloader();
            DownloadOperation download = downloader.CreateDownload(source, destinationFile);

            Log(String.Format(CultureInfo.CurrentCulture, "Downloading {0} to {1} with {2} priority, {3}",
                source.AbsoluteUri, destinationFile.Name, priority, download.Guid));

            download.Priority = priority;

            if (!requestUnconstrainedDownload)
            {
                // Attach progress and completion handlers.
                await HandleDownloadAsync(download, true);
                return;
            }

            List<DownloadOperation> requestOperations = new List<DownloadOperation>();
            requestOperations.Add(download);

            // If the app isn't actively being used, at some point the system may slow down or pause long running
            // downloads. The purpose of this behavior is to increase the device's battery life.
            // By requesting unconstrained downloads, the app can request the system to not suspend any of the
            // downloads in the list for power saving reasons.
            // Use this API with caution since it not only may reduce battery life, but it may show a prompt to
            // the user.
            UnconstrainedTransferRequestResult result;
            try
            {
                result = await BackgroundDownloader.RequestUnconstrainedDownloadsAsync(requestOperations);
            }
            catch (NotImplementedException)
            {
                rootPage.NotifyUser(
                    "BackgroundDownloader.RequestUnconstrainedDownloadsAsync is not supported in Windows Phone.",
                    NotifyType.ErrorMessage);
                return;
            }

            Log(String.Format(CultureInfo.CurrentCulture, "Request for unconstrained downloads has been {0}",
                (result.IsUnconstrained ? "granted" : "denied")));

            await HandleDownloadAsync(download, true);
        }

        private async Task StartDownload(Uri source, string destination)
        {
            StorageFile destinationFile;
            try
            {
                destinationFile = await KnownFolders.MusicLibrary.CreateFileAsync(
                    destination, CreationCollisionOption.GenerateUniqueName);
            }
            catch (FileNotFoundException ex)
            {
                rootPage.NotifyUser("Error while creating file: " + ex.Message, NotifyType.ErrorMessage);
                return;
            }

            BackgroundDownloader downloader = new BackgroundDownloader();
            DownloadOperation download = downloader.CreateDownload(source, destinationFile);

            download.Priority = BackgroundTransferPriority.Default;
            
            // Attach progress and completion handlers.
            await HandleDownloadAsync(download, true);
        }

        private void StartDownload_Click(object sender, RoutedEventArgs e)
        {
            StartDownload(BackgroundTransferPriority.Default, false);
        }

        private void StartHighPriorityDownload_Click(object sender, RoutedEventArgs e)
        {
            StartDownload(BackgroundTransferPriority.High, false);
        }

        private void StartUnconstrainedDownload_Click(object sender, RoutedEventArgs e)
        {
            StartDownload(BackgroundTransferPriority.Default, true);
        }

        private void PauseAll_Click(object sender, RoutedEventArgs e)
        {
            Log("Downloads: " + _activeDownloads.Count);

            foreach (DownloadOperation download in _activeDownloads)
            {
                if (download.Progress.Status == BackgroundTransferStatus.Running)
                {
                    download.Pause();
                    Log("Paused: " + download.Guid);
                }
                else
                {
                    Log(String.Format(CultureInfo.CurrentCulture, "Skipped: {0}, Status: {1}", download.Guid,
                        download.Progress.Status));
                }
            }
        }

        private void ResumeAll_Click(object sender, RoutedEventArgs e)
        {
            Log("Downloads: " + _activeDownloads.Count);

            foreach (DownloadOperation download in _activeDownloads)
            {
                if (download.Progress.Status == BackgroundTransferStatus.PausedByApplication)
                {
                    download.Resume();
                    Log("Resumed: " + download.Guid);
                }
                else
                {
                    Log(String.Format(CultureInfo.CurrentCulture, "Skipped: {0}, Status: {1}", download.Guid,
                        download.Progress.Status));
                }
            }
        }

        private void CancelAll_Click(object sender, RoutedEventArgs e)
        {
            Log("Canceling Downloads: " + _activeDownloads.Count);

            _cts.Cancel();
            _cts.Dispose();

            foreach (var downloadOperation in _activeDownloads)
            {
                downloadOperation.AttachAsync().Cancel();
            }
            // Re-create the CancellationTokenSource and activeDownloads for future downloads.
            _cts = new CancellationTokenSource();
            _activeDownloads = new List<DownloadOperation>();
        }

        // Note that this event is invoked on a background thread, so we cannot access the UI directly.
        private void DownloadProgress(DownloadOperation download)
        {
            MarshalLog(String.Format(CultureInfo.CurrentCulture, "Progress: {0}, Status: {1}", download.Guid,
                download.Progress.Status));

            double percent = 100;
            if (download.Progress.TotalBytesToReceive > 0)
            {
                percent = download.Progress.BytesReceived * 100 / download.Progress.TotalBytesToReceive;
            }

            var match = _viewModel.DownloadItems.FirstOrDefault(di => di.Uri == download.RequestedUri);

            var ignore = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                //Progress.Value = percent;
                if (match != null)
                {
                    match.Progress = percent;
                    match.Status = download.Progress.Status.ToString();
                }
            });

            MarshalLog(String.Format(CultureInfo.CurrentCulture, " - Transfered bytes: {0} of {1}, {2}%",
                download.Progress.BytesReceived, download.Progress.TotalBytesToReceive, percent));

            if (download.Progress.HasRestarted)
            {
                MarshalLog(" - Download restarted");
            }

            if (download.Progress.HasResponseChanged)
            {
                // We've received new response headers from the server.
                MarshalLog(" - Response updated; Header count: " + download.GetResponseInformation().Headers.Count);

                // If you want to stream the response data this is a good time to start.
                // download.GetResultStreamAt(0);
            }
        }

        private async Task HandleDownloadAsync(DownloadOperation download, bool start)
        {
            try
            {
                //LogStatus("Running: " + download.Guid, NotifyType.StatusMessage);

                // Store the download so we can pause/resume.
                _activeDownloads.Add(download);

                Progress<DownloadOperation> progressCallback = new Progress<DownloadOperation>(DownloadProgress);
                if (start)
                {
                    // Start the download and attach a progress handler.
                    await download.StartAsync().AsTask(_cts.Token, progressCallback);
                }
                else
                {
                    // The download was already running when the application started, re-attach the progress handler.
                    await download.AttachAsync().AsTask(progressCallback);
                }

                ResponseInformation response = download.GetResponseInformation();

                //LogStatus(String.Format(CultureInfo.CurrentCulture, "Completed: {0}, Status Code: {1}",
                //    download.Guid, response.StatusCode), NotifyType.StatusMessage);
            }
            catch (TaskCanceledException)
            {
                //MarshalLog("Canceled: " + download.Guid, NotifyType.StatusMessage);
            }
            catch (Exception ex)
            {
                if (!IsExceptionHandled("Execution error", ex, download))
                {
                    throw;
                }
            }
            finally
            {
                _activeDownloads.Remove(download);
            }
        }

        private bool IsExceptionHandled(string title, Exception ex, DownloadOperation download = null)
        {
            WebErrorStatus error = BackgroundTransferError.GetStatus(ex.HResult);
            if (error == WebErrorStatus.Unknown)
            {
                return false;
            }

            if (download == null)
            {
                LogStatus(String.Format(CultureInfo.CurrentCulture, "Error: {0}: {1}", title, error),
                    NotifyType.ErrorMessage);
            }
            else
            {
                LogStatus(String.Format(CultureInfo.CurrentCulture, "Error: {0} - {1}: {2}", download.Guid, title,
                    error), NotifyType.ErrorMessage);
            }

            return true;
        }

        // When operations happen on a background thread we have to marshal UI updates back to the UI thread.
        private void MarshalLog(string value)
        {
            var ignore = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                Log(value);
            });
        }

        private void Log(string message)
        {
            outputField.Text += message + "\r\n";
        }

        private void LogStatus(string message, NotifyType type)
        {
            rootPage.NotifyUser(message, type);
            Log(message);
        }

        private async void Opml_Click(object sender, RoutedEventArgs e)
        {
            var f = new Feeds();

            var urls = await f.FeedUrlsFromOpml();
            foreach (var url in urls)
            {
                var xml = await f.GetFeedXml(new Uri(url));

                var items = f.Thing(xml);

                var item = items.First();

                DownloadItem downloadItem = new DownloadItem();
                downloadItem.Title = item.Title.Text;

                _viewModel.DownloadItems.Add(downloadItem);

                foreach (var link in item.Links.Where(ln => ln.Relationship == "enclosure"))
                {
                    Debug.WriteLine("Link Title: " + link.Title);
                    Debug.WriteLine("URI: " + link.Uri);
                    Debug.WriteLine("RelationshipType: " + link.Relationship);
                    Debug.WriteLine("MediaType: " + link.MediaType);
                    Debug.WriteLine("Length: " + link.Length);

                    downloadItem.Uri = link.Uri;

                    var fileName = Path.GetFileName(link.Uri.LocalPath);

                    var ignore = Task.Run( async () => await StartDownload(link.Uri, fileName));
                }
            }
        }
    }

    public class DownloadViewModel
    {
        public ObservableCollection<DownloadItem> DownloadItems { get; private set; }

        public DownloadViewModel()
        {
            DownloadItems = new ObservableCollection<DownloadItem>();
        }
    }

    public class DownloadItem : INotifyPropertyChanged
    {
        private double _progress;
        private string _title;
        private Uri _uri;
        private string _status;

        public string Status
        {
            get { return _status; }
            set
            {
                if (value == _status) return;
                _status = value;
                OnPropertyChanged();
            }
        }

        public double Progress
        {
            get { return _progress; }
            set
            {
                if (value.Equals(_progress)) return;
                _progress = value;
                OnPropertyChanged();
            }
        }

        public string Title
        {
            get { return _title; }
            set
            {
                if (value == _title) return;
                _title = value;
                OnPropertyChanged();
            }
        }

        public Uri Uri
        {
            get { return _uri; }
            set
            {
                if (Equals(value, _uri)) return;
                _uri = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }


    public enum NotifyType
    {
        StatusMessage,
        ErrorMessage
    };
}
