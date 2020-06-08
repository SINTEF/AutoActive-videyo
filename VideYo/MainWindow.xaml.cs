using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Events;

namespace VideYo
{
    // Icon made by Eucalyp from https://www.flaticon.com/free-icon/scalable_2171033#term=resize&page=1&position=6
    // https://www.flaticon.com/authors/Eucalyp

    public partial class MainWindow : Window
    {
        private readonly IList<FileItem> _items = new ObservableCollection<FileItem>();
        private TimeSpan _currentLength;
        private TimeSpan _totalLength;

        public MainWindow()
        {
            InitializeComponent();

            ListBox.DataContext = _items;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            HeightBox.Text = Properties.Settings.Default.VideoHeight.ToString();
            var videoQualityText = Properties.Settings.Default.VideoQuality;
            for (var index = 0; index < QualityBox.Items.Count; index++)
            {
                var item = QualityBox.Items[index] as ComboBoxItem;
                if (item?.Content.ToString() == videoQualityText)
                {
                    QualityBox.SelectedIndex = index;
                }
            }
        }

        private async Task EnqueueFile(string fileName)
        {
            try
            {
                var info = await MediaInfo.Get(fileName);

                _totalLength += info.Duration;

                var count = info.VideoStreams.Count();
                if (count == 0)
                {
                    MessageBox.Show($"Could not find a video stream in\n{fileName}", "Skipping file", MessageBoxButton.OK);
                    return;
                }

                if (count > 1)
                {
                    MessageBox.Show(
                        $"Multiple streams found in file. Only the first will be processed.\n{fileName}", "Warning",
                        MessageBoxButton.OK);
                }

                _items.Add(new FileItem { Path = fileName, Name = Path.GetFileName(fileName), MediaInfo = info });
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show($"Could not load file:\n{fileName}\n{ex.Message}", "Error", MessageBoxButton.OK);
            }
        }

        private async void Add_Clicked(object sender, RoutedEventArgs e)
        {
            await EnsureFfmpeg();
            var fileDialog = new OpenFileDialog {Multiselect = true};

            var result = fileDialog.ShowDialog();
            if (!(result.HasValue && result.Value))
            {
                return;
            }

            foreach (var fileName in fileDialog.FileNames)
            {
                await EnqueueFile(fileName);
            }
        }

        private void Remove_Clicked(object sender, RoutedEventArgs e)
        {
            if (ListBox.SelectedItems.Count == 0)
            {
                MessageBox.Show("Select items to remove first", "Error.");
                return;
            }
            var selectedItems = ListBox.SelectedItems.Cast<FileItem>().ToList();
            foreach (var item in selectedItems)
            {
                _items.Remove(item);
            }
        }

        private string GetSaveName(string fileName, bool batchMode)
        {
            var origName = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrEmpty(origName))
            {
                return "compressed.mp4";
            }

            return batchMode || _items.Count == 1 ? $"{origName}_compressed.mp4" : $"{origName}_combined.mp4";
        }

        private string GetSavePath(string saveDir, string path)
        {
            return Path.Combine(saveDir, GetSaveName(path, true));
        }

        private async Task ExportVideos(bool batchMode)
        {
            if (!_items.Any())
            {
                MessageBox.Show("No videos selected.");
                return;
            }

            await EnsureFfmpeg();

            int? height = null;
            if (int.TryParse(HeightBox.Text, out var heightText) && heightText > 0)
            {
                height = heightText;
            }

            var preset = QualityBox.Text;

            var fileName = GetSaveName(_items.First().Name, batchMode);

            var saveFileDialog = new SaveFileDialog
            {
                DefaultExt = ".mp4",
                OverwritePrompt = true,
                FileName = fileName,
                Filter = "Video Files|*.mp4"
            };

            var dialogResult = saveFileDialog.ShowDialog();
            if (!(dialogResult.HasValue && dialogResult.Value))
            {
                return;
            }

            var outputPath = saveFileDialog.FileName;
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            var saveDir = Path.GetDirectoryName(outputPath);
            if (batchMode)
            {
                if (string.IsNullOrEmpty(saveDir))
                {
                    MessageBox.Show("Could not get save directory.", "Error");
                    return;
                }

                var existingFiles = _items.Select(item => GetSavePath(saveDir, item.Path))
                    .Where(File.Exists).ToArray();

                if (existingFiles.Any())
                {
                    var saveOk = MessageBox.Show($"The following files will be overwritten by this operation:\n{string.Join("\n",existingFiles)}\n Would you like to continue?", "Warning", MessageBoxButton.YesNo);
                    if (saveOk != MessageBoxResult.Yes) return;
                }

                foreach (var existingFile in existingFiles)
                {
                    File.Delete(existingFile);
                }
            }

            _totalLength = new TimeSpan();
            _currentLength = new TimeSpan();
            foreach (var item in _items)
            {
                _totalLength += item.MediaInfo.Duration;
            }

            if (!batchMode)
            {
                await CombineFiles(preset, height, outputPath, _items);
                MessageBox.Show($"Saved file:{outputPath}", "Success");
            }
            else
            {
                var outFiles = new List<string>();
                foreach (var item in _items)
                {
                    var savePath = GetSavePath(saveDir, item.Path);
                    await CombineFiles(preset, height, savePath, new List<FileItem> {item});
                    _currentLength += item.MediaInfo.Duration;
                    outFiles.Add(Path.GetFileName(savePath));
                }

                MessageBox.Show($"Saved files:\n{string.Join("\n", outFiles)}\n", "Success");
            }


        }

        private async Task CombineFiles(string preset, int? height, string outputPath, ICollection<FileItem> items)
        {
            var sb = new StringBuilder();

            foreach (var item in items)
            {
                sb.Append($" -i \"{item.Path}\" ");
            }

            sb.Append($" -c:v libx264 -preset {preset} ");
            sb.Append(" -filter_complex \"");
            for (var i = 0; i < items.Count; i++)
            {
                sb.Append($"[{i}:v] ");
            }

            sb.Append($" concat=n={items.Count}:v=1 [v] ");

            sb.Append(height.HasValue
                ? $"; [v]scale=-1:{height.Value}:force_original_aspect_ratio=1[v2]\" -map \"[v2]\""
                : "\" -map \"[v]\" ");

            var conversion = Conversion.New();
            conversion.AddParameter(sb.ToString());
            conversion.AddParameter("-map_metadata 0");
            conversion.SetOutput(outputPath);
            conversion.OnProgress += ConversionOnOnProgress;
            conversion.OnDataReceived += ConversionOnOnDataReceived;
            await conversion.Start();
        }

        private async void Process_Clicked(object sender, RoutedEventArgs e)
        {
            await ExportVideos(false);
        }
        private async void Batch_Clicked(object sender, RoutedEventArgs e)
        {
            await ExportVideos(true);
        }

        private void ConversionOnOnDataReceived(object sender, DataReceivedEventArgs e)
        {
            Console.WriteLine(e.Data);
        }

        private void ConversionOnOnProgress(object sender, ConversionProgressEventArgs args)
        {
            Application.Current.Dispatcher?.Invoke(() =>
                ProgressBar.Value = 100 * (_currentLength.Duration().TotalSeconds + args.Duration.TotalSeconds) / _totalLength.TotalSeconds);
        }

        private async Task EnsureFfmpeg()
        {
            if (File.Exists("ffmpeg.exe"))
            {
                return;
            }

            MessageBox.Show("ffmpeg.exe not found, downloading. This might take some time.");
            await FFmpeg.GetLatestVersion(true);
        }

        private async void ListBox_OnDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files)) return;

            foreach (var file in files)
            {
                await EnqueueFile(file);
            }
        }

        private void SaveSettingsClick(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(HeightBox.Text, out var heightText))
            {
                Properties.Settings.Default.VideoHeight = heightText;
            }
            else
            {
                MessageBox.Show($"Could not store video height. Value must be integer but was:{HeightBox.Text}",
                    "Error");
            }

            Properties.Settings.Default.VideoQuality = QualityBox.Text;
            Properties.Settings.Default.Save();
        }

        private void Clear_OnClick(object sender, RoutedEventArgs e)
        {
            _items.Clear();
        }
    }

    public class FileItemDragAndDropListBox : DragAndDropListBox<FileItem> { }

    public class FileItem
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public IMediaInfo MediaInfo { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }
}
