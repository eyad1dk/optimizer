using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using Microsoft.Win32;
namespace ForgePC;
public sealed class PageVisibilityConverter : IValueConverter
{
 public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>Equals(value?.ToString(),parameter?.ToString())?Visibility.Visible:Visibility.Collapsed;
 public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
}
public sealed class NavigationTemplateSelector : DataTemplateSelector
{
 public override DataTemplate? SelectTemplate(object item,DependencyObject container)=>item is string page?((FrameworkElement)container).TryFindResource("Page-"+page) as DataTemplate:null;
}
public sealed class DesktopInteraction : IUserInteraction
{
 public string? ChooseGame(){var picker=new OpenFileDialog {Title="Add a game executable",Filter="Windows executable (*.exe)|*.exe",CheckFileExists=true};return picker.ShowDialog()==true?picker.FileName:null;}
 public string? ChooseWorkspace(){var picker=new OpenFolderDialog {Title="Choose coding workspace"};return picker.ShowDialog()==true?picker.FolderName:null;}
 public async Task<string?> ReadBenchmark(){var picker=new OpenFileDialog {Filter="Frame-time CSV (*.csv)|*.csv",CheckFileExists=true};if(picker.ShowDialog()!=true)return null;using var stream=File.OpenRead(picker.FileName);if(stream.Length>8*1024*1024)throw new InvalidDataException("CSV exceeds 8 MB.");using var reader=new StreamReader(stream);return await reader.ReadToEndAsync();}
 public Task<string?> ReadProfile(){var picker=new OpenFileDialog {Filter="EZoptimizer profile (*.json)|*.json",CheckFileExists=true};if(picker.ShowDialog()!=true)return Task.FromResult<string?>(null);if(new FileInfo(picker.FileName).Length>65536)throw new InvalidDataException("Profile exceeds 64 KB.");return File.ReadAllTextAsync(picker.FileName)!;}
 public async Task WriteText(string text,string name){var picker=new SaveFileDialog {FileName=name,Filter="JSON (*.json)|*.json",AddExtension=true};if(picker.ShowDialog()==true)await File.WriteAllTextAsync(picker.FileName,text);}
 public bool Confirm(string text,string title)
 {
  var dialog=new Window{Title=title,Width=620,Height=460,MinWidth=360,MinHeight=240,Owner=Application.Current.MainWindow,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=(Brush)Application.Current.FindResource("Canvas"),Foreground=(Brush)Application.Current.FindResource("Ink")};
  var dock=new DockPanel{Margin=new Thickness(24)};var buttons=new WrapPanel();DockPanel.SetDock(buttons,Dock.Bottom);var yes=new Button{Content="Continue",IsDefault=true};var no=new Button{Content="Cancel",IsCancel=true};yes.Click+=(_,_)=>dialog.DialogResult=true;buttons.Children.Add(yes);buttons.Children.Add(no);dock.Children.Add(buttons);dock.Children.Add(new ScrollViewer{Content=new TextBlock{Text=text},VerticalScrollBarVisibility=ScrollBarVisibility.Auto});dialog.Content=dock;return dialog.ShowDialog()==true;
 }
 public void ApplyTheme(string theme)
 {
  var high=SystemParameters.HighContrast||theme=="High contrast";
  bool light=theme=="Light" || theme=="Windows" && (int?)(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",0))==1;
  var colors=high?new[]{"#000000","#000000","#202020","#FFFFFF","#FFFFFF","#FFFFFF","#FFFF00","#000000"}:light?new[]{"#F3F5F2","#FFFFFF","#E8EEE8","#C5CEC5","#19241E","#526158","#245C3C","#FFFFFF"}:new[]{"#101314","#191D1F","#252B2D","#303638","#F3F6F5","#A6AFAD","#B6F3CD","#142C20"};
  var keys=new[]{"Canvas","Surface","Raised","Line","Ink","Muted","Accent","OnAccent"};
  for(int i=0;i<keys.Length;i++)Application.Current.Resources[keys[i]]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
  if(SystemParameters.HighContrast){Application.Current.Resources["Canvas"]=SystemColors.WindowBrush;Application.Current.Resources["Surface"]=SystemColors.WindowBrush;Application.Current.Resources["Raised"]=SystemColors.ControlBrush;Application.Current.Resources["Ink"]=SystemColors.WindowTextBrush;Application.Current.Resources["Muted"]=SystemColors.WindowTextBrush;Application.Current.Resources["Accent"]=SystemColors.HighlightBrush;Application.Current.Resources["OnAccent"]=SystemColors.HighlightTextBrush;Application.Current.Resources["Line"]=SystemColors.WindowTextBrush;}
 }
}
public partial class MainWindow : Window
{
 public MainViewModel ViewModel {get;}
 private readonly DispatcherTimer timer=new();private bool closeReady;
 public MainWindow(string dataRoot,bool recoveryOnly=false)
 {
  InitializeComponent();ViewModel=new(dataRoot,new DesktopInteraction(),recoveryOnly);DataContext=ViewModel;
  timer.Interval=TimeSpan.FromSeconds(2);timer.Tick+=async (_,_)=>{ViewModel.MetricsEnabled=WindowState!=WindowState.Minimized;timer.Interval=TimeSpan.FromSeconds(IsActive&&ViewModel.MetricsEnabled?2:10);await ViewModel.TickAsync();};
  ViewModel.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(MainViewModel.Status))UIElementAutomationPeer.CreatePeerForElement(OperationStatus)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);if(e.PropertyName==nameof(MainViewModel.Page))PageScroll.ScrollToTop();};
  Closing+=ClosingWindow;Closed+=(_,_)=>{timer.Stop();SystemParameters.StaticPropertyChanged-=SystemChanged;SystemEvents.UserPreferenceChanged-=PreferenceChanged;ViewModel.Dispose();};SystemParameters.StaticPropertyChanged+=SystemChanged;SystemEvents.UserPreferenceChanged+=PreferenceChanged;
 }
 public async Task InitializeAsync(bool poll=true){await ViewModel.InitializeAsync();if(poll)timer.Start();}
 private void MinimizeWindow(object sender,RoutedEventArgs e)=>System.Windows.SystemCommands.MinimizeWindow(this);
 private void MaximizeWindow(object sender,RoutedEventArgs e){if(WindowState==WindowState.Maximized)System.Windows.SystemCommands.RestoreWindow(this);else System.Windows.SystemCommands.MaximizeWindow(this);}
 private void CloseWindow(object sender,RoutedEventArgs e)=>Close();
 public void VerifyPageLayout()
 {
  IEnumerable<FrameworkElement> Elements(DependencyObject root){for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is FrameworkElement f)yield return f;foreach(var nested in Elements(child))yield return nested;}}
  var pages=Elements(Root).Where(e=>e.Tag?.ToString()?.StartsWith("Page-")==true && e.Visibility==Visibility.Visible).ToList();
  if(pages.Count!=1||pages[0].ActualWidth<=0||pages[0].Tag.ToString()!="Page-"+ViewModel.Page)throw new InvalidOperationException("Navigation did not produce exactly one measurable page.");
  if(System.Windows.Automation.AutomationProperties.GetName(OperationStatus)!="Operation status")throw new InvalidOperationException("Status automation name is missing.");
  foreach(var button in Elements(Root).OfType<Button>())
  {
   if(button.Command==null&&System.Windows.Automation.AutomationProperties.GetName(button) is not("Minimize" or "Maximize" or "Close"))throw new InvalidOperationException("Unbound button on "+ViewModel.Page+": "+button.Content);
  }
  PageScroll.ScrollToEnd();PageScroll.UpdateLayout();
  if(PageScroll.ScrollableHeight>1&&PageScroll.VerticalOffset<1)throw new InvalidOperationException("Page scrolling did not reach content.");
  PageScroll.ScrollToTop();PageScroll.UpdateLayout();
  foreach(var panel in Elements(Root).OfType<AdaptivePanel>())
   foreach(UIElement child in panel.Children)
   {
    var bounds=child.TransformToAncestor(panel).TransformBounds(new Rect(new Point(),child.RenderSize));
    if(bounds.Left < -1 || bounds.Right > panel.ActualWidth+1)throw new InvalidOperationException("A responsive card exceeds its available width.");
   }
 }
 public void VerifyExpandedActions()
 {
  IEnumerable<Expander> Expanders(DependencyObject root){for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is Expander e)yield return e;foreach(var nested in Expanders(child))yield return nested;}}
  var expanders=Expanders(Root).ToArray();var previous=expanders.Select(e=>e.IsExpanded).ToArray();
  try{foreach(var e in expanders)e.SetCurrentValue(Expander.IsExpandedProperty,true);Root.Measure(new Size(850,600));Root.Arrange(new Rect(0,0,850,600));Root.UpdateLayout();VerifyPageLayout();}
  finally{for(int i=0;i<expanders.Length;i++)expanders[i].SetCurrentValue(Expander.IsExpandedProperty,previous[i]);}
 }
 private void SystemChanged(object? sender,PropertyChangedEventArgs e){if(e.PropertyName==nameof(SystemParameters.HighContrast))new DesktopInteraction().ApplyTheme(ViewModel.Theme);}
 private void PreferenceChanged(object sender,UserPreferenceChangedEventArgs e){if(ViewModel.Theme=="Windows" && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)Dispatcher.InvokeAsync(()=>new DesktopInteraction().ApplyTheme(ViewModel.Theme));}
 private async void ClosingWindow(object? sender,CancelEventArgs e){if(closeReady)return;if(ViewModel.Busy||ViewModel.Tools.Busy||ViewModel.Actions.Busy){e.Cancel=true;ViewModel.Status="Wait for the reviewed operation and recovery to finish before closing.";return;}e.Cancel=true;await ViewModel.CloseSessionAsync();closeReady=true;Close();}
}
