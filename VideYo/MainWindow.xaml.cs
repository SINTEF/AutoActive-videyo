using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Events;

namespace VideYo
{
    // Icon made by Eucalyp from https://www.flaticon.com/free-icon/scalable_2171033#term=resize&page=1&position=6
    // https://www.flaticon.com/authors/Eucalyp

    public partial class MainWindow : Window
    {
        private readonly IList<FileItem> _items = new ObservableCollection<FileItem>();
        private readonly Task _ffmpegGetterTask;
        private TimeSpan _totalLength;

        public MainWindow()
        {
            InitializeComponent();

            listBox.DataContext = _items;

            _ffmpegGetterTask = FFmpeg.GetLatestVersion();
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
            var fileDialog = new OpenFileDialog {Multiselect = true};

            await _ffmpegGetterTask;

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
            await _ffmpegGetterTask;

            int? height = null;
            if (int.TryParse(HeightBox.Text, out var heightText))
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
    }

    public class FileItemDragAndDropListBox : DragAndDropListBox<FileItem> { }

    public class DragAndDropListBox<T> : ListBox
       where T : class
    {
        private Point _dragStartPoint;
        private bool _isDragging;

        private static TP FindVisualParent<TP>(DependencyObject child) where TP : DependencyObject
        {
            while (true)
            {
                var parentObject = VisualTreeHelper.GetParent(child);
                switch (parentObject)
                {
                    case null:
                        return null;
                    case TP parent:
                        return parent;
                    default:
                        child = parentObject;
                        break;
                }
            }
        }

        public DragAndDropListBox()
        {
            PreviewMouseMove += ListBox_PreviewMouseMove;

            var style = new Style(typeof(ListBoxItem));

            style.Setters.Add(new Setter(AllowDropProperty, true));

            style.Setters.Add(new EventSetter(PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(ListBoxItem_PreviewMouseLeftButtonDown)));

            style.Setters.Add(new EventSetter(DropEvent, new DragEventHandler(ListBoxItem_Drop)));

            ItemContainerStyle = style;
        }

        private void ListBox_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;

            var point = e.GetPosition(null);
            var diff = _dragStartPoint - point;
            if (e.LeftButton != MouseButtonState.Pressed ||
                (!(Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance) &&
                 !(Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance))) return;

            var lbi = FindVisualParent<ListBoxItem>((DependencyObject)e.OriginalSource);
            if (lbi != null)
            {
                DragDrop.DoDragDrop(lbi, lbi.DataContext, DragDropEffects.Move);
            }
        }

        private void ListBoxItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            _dragStartPoint = e.GetPosition(null);
        }

        private void ListBoxItem_Drop(object sender, DragEventArgs e)
        {
            _isDragging = false;
            if (!(sender is ListBoxItem listBoxItem)) return;

            var source = e.Data.GetData(typeof(T)) as T;
            var target = listBoxItem.DataContext as T;
            if (source == null || target == null)
                return;

            var sourceIndex = Items.IndexOf(source);
            var targetIndex = Items.IndexOf(target);

            Move(source, sourceIndex, targetIndex);
        }

        private void Move(T source, int sourceIndex, int targetIndex)
        {
            if (sourceIndex < targetIndex)
            {
                if (!(DataContext is IList<T> items)) return;

                items.Insert(targetIndex + 1, source);
                items.RemoveAt(sourceIndex);
            }
            else
            {
                if (!(DataContext is IList<T> items)) return;

                var removeIndex = sourceIndex + 1;
                if (items.Count + 1 <= removeIndex) return;

                items.Insert(targetIndex, source);
                items.RemoveAt(removeIndex);
            }
        }
    }

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
