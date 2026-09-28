using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AutoMagic.Application.ExchangeRates;
using AutoMagic.Application.Ozon;
using AutoMagic.Application.Ozon.Mapping;
using AutoMagic.Application.Search;
using AutoMagic.Desktop;
using AutoMagic.Desktop.ViewModels;
using AutoMagic.Infrastructure.Collection;

static class UiCheck
{
    public static void Run(RuleReviewService service, RuleReviewSession session, string root)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var vm = new MainViewModel(Stub<ISearchBridge>(), Stub<IExchangeRateService>(), Stub<IOzonSchemaService>(),
                    Stub<ILocalOzonCategoryCatalog>(), Stub<IOzonTestSettingsStore>(), Stub<IQwenTestSettingsStore>(),
                    new CollectionSnapshotStore(Path.Combine(root,"collections")), service);
                foreach (var row in session.Rows) vm.ReviewRows.Add(row);
                vm.SelectedReviewRow = vm.ReviewRows[0];
                vm.IsPricingExpanded=false;
                vm.SelectedFieldMatchingTabIndex=0;
                vm.ReviewStatus = "完善中文填写，勾选已核对，然后确认解析并保存规则。";
                var window = new MainWindow(vm);
                var tab = Descendants(window).OfType<TabItem>().Single(t=>Equals(t.Header,"字段匹配"));
                tab.IsSelected=true;
                window.Measure(new Size(1280,900)); window.Arrange(new Rect(0,0,1280,900)); window.UpdateLayout();
                var form = Descendants(tab).OfType<TabItem>().Single(t=>Equals(t.Header,"匹配与人工填写"));
                form.IsSelected=true; window.UpdateLayout();
                var grid=Descendants(form).OfType<DataGrid>().Single();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                grid.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.UpdateTarget();
                if(grid.IsReadOnly || !ReferenceEquals(grid.ItemsSource,vm.ReviewRows)) throw new Exception($"Form binding failed: readonly={grid.IsReadOnly}, context={grid.DataContext?.GetType().Name}, items={grid.ItemsSource?.GetType().Name}");
                var content=(FrameworkElement)window.Content;
                if(content is Panel panel) panel.Background=window.Background;
                window.Content=null;
                content.DataContext=vm;
                content.Resources=window.Resources;
                content.Measure(new Size(1280,900)); content.Arrange(new Rect(0,0,1280,900)); content.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var image=new RenderTargetBitmap(1280,900,96,96,PixelFormats.Pbgra32); image.Render(content);
                var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                using(var file=File.Create(Path.Combine(root,"review-form.png"))) png.Save(file);
                window.Close();
            }
            catch(Exception error) { failure=error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(20))) throw new Exception("UI check timed out");
        if(failure is not null) throw failure;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        { yield return child; foreach(var item in Descendants(child)) yield return item; }
    }
    private static T Stub<T>() where T:class => DispatchProxy.Create<T,EmptyProxy>();
    public class EmptyProxy:DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            var type=method!.ReturnType;
            if(type==typeof(void)) return null;
            if(type==typeof(Task)) return Task.CompletedTask;
            return type.IsValueType?Activator.CreateInstance(type):null;
        }
    }
}
