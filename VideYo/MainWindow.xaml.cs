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
        private TimeSpan _totalLength;

        public MainWindow()
        {
            InitializeComponent();

            listBox.DataContext = _items;
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

        private async void Process_Clicked(object sender, RoutedEventArgs e)
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

            var saveFileDialog = new SaveFileDialog
            {
                DefaultExt = ".mp4", OverwritePrompt = true, FileName = "combined.mp4", Filter = "Video Files|*.mp4"
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

            var conversion = Conversion.New();
            var sb = new StringBuilder();

            _totalLength = new TimeSpan();
            foreach (var item in _items)
            {
               sb.Append($" -i \"{item.Path}\" ");
               _totalLength += item.MediaInfo.Duration;
            }

            sb.Append($" -c:v libx264 -preset {preset} ");
            sb.Append(" -filter_complex \"");
            for (var i = 0; i < _items.Count; i++)
            {
                sb.Append($"[{i}:v] ");
            }

            sb.Append($" concat=n={_items.Count}:v=1 [v] ");

            sb.Append(height.HasValue
                ? $"; [v]scale=-1:{height.Value}:force_original_aspect_ratio=1[v2]\" -map \"[v2]\""
                : "\" -map \"[v]\" ");

            conversion.AddParameter(sb.ToString());
            conversion.SetOutput(outputPath);
            conversion.OnProgress += ConversionOnOnProgress;
            conversion.OnDataReceived += ConversionOnOnDataReceived;
            await conversion.Start();
            
            MessageBox.Show($"Saved file:{outputPath}", "Success");
        }

        private void ConversionOnOnDataReceived(object sender, DataReceivedEventArgs e)
        {
            Console.WriteLine(e.Data);
        }

        private void ConversionOnOnProgress(object sender, ConversionProgressEventArgs args)
        {
            Application.Current.Dispatcher?.Invoke(() =>
                ProgressBar.Value = 100 * args.Duration.TotalSeconds / _totalLength.TotalSeconds);
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
