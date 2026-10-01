using System.Runtime.CompilerServices;
using System.Windows;
using AresToys.Editor.ViewModels;
using AresToys.Editor.Views;
using Xunit;

namespace AresToys.App.Tests;

public class EditorWindowLeakTests
{
    /// <summary>Regression: DependencyPropertyDescriptor.AddValueChanged hooks on the colour
    /// swatches kept every closed editor (and its full-size image) alive for the life of the
    /// process. A closed editor must be collectable.</summary>
    [Fact]
    public void ClosedEditorWindow_IsGarbageCollected()
    {
        Exception? failure = null;
        var collected = false;
        var thread = new Thread(() =>
        {
            try
            {
                // The editor XAML needs the app's merged theme dictionaries and pack:// resources.
                if (Application.Current is null)
                {
                    var app = new AresToys.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.InitializeComponent();
                }
                var weak = OpenAndCloseEditor();
                for (var i = 0; i < 5 && weak.IsAlive; i++)
                {
                    // Let the dispatcher drain the queued close/layout work that briefly
                    // references the window, then collect.
                    DrainDispatcher();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                collected = !weak.IsAlive;
            }
            catch (Exception ex) { failure = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.True(collected, "EditorWindow is still reachable after Close().");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndCloseEditor()
    {
        var window = new EditorWindow(new EditorViewModel())
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
        };
        // Headless, Wpf.Ui's TitleBar fails to hook the hwnd from ContentRendered ("Hwnd of
        // zero is not valid"). Unrelated to what this test checks, so it is swallowed here.
        window.Dispatcher.UnhandledException += (_, e) =>
        {
            if (e.Exception is ArgumentException && e.Exception.StackTrace?.Contains("Wpf.Ui.Controls.TitleBar", StringComparison.Ordinal) == true)
                e.Handled = true;
        };
        var rendered = false;
        window.ContentRendered += (_, _) => rendered = true;
        window.Show();
        // Close only after the first render, as in real use (Loaded/Unloaded both run).
        for (var i = 0; i < 300 && !rendered; i++)
        {
            DrainDispatcher();
            Thread.Sleep(10);
        }
        Assert.True(rendered, "Editor window never rendered.");
        window.Close();
        return new WeakReference(window);
    }

    private static void DrainDispatcher() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
}
