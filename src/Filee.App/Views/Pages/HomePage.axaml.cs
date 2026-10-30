// This workspace stages local documents. Conversion starts only from its explicit action button.
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Microsoft.Extensions.Logging;

namespace Filee.App.Views.Pages;

public partial class HomePage : UserControl
{
    private bool _pickerOpen;

    public HomePage()
    {
        InitializeComponent();
        DropZone.DoubleTapped += async (_, e) =>
        {
            e.Handled = true;
            await ChooseFilesAsync();
        };
        DragDrop.AddDragEnterHandler(DropZone, (_, e) =>
        {
            DropZone.Classes.Add("over");
            e.DragEffects = DraggedFiles.Offered(e.DataTransfer) ? DragDropEffects.Copy : DragDropEffects.None;
        });
        DragDrop.AddDragOverHandler(DropZone, (_, e) =>
            e.DragEffects = DraggedFiles.Offered(e.DataTransfer) ? DragDropEffects.Copy : DragDropEffects.None);
        DragDrop.AddDragLeaveHandler(DropZone, (_, _) => DropZone.Classes.Remove("over"));
        DragDrop.AddDropHandler(DropZone, OnDrop);
    }

    private async void OnChooseFiles(object? sender, RoutedEventArgs e) => await ChooseFilesAsync();

    private async Task ChooseFilesAsync()
    {
        if (_pickerOpen || DataContext is not HomePageViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
            return;
        _pickerOpen = true;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = vm.Workspace.AddFilesText,
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("HTML · PDF · PPTX · DOCX · TXT")
                    {
                        Patterns = ["*.html", "*.htm", "*.xhtml", "*.pdf", "*.pptx", "*.docx", "*.txt"],
                        MimeTypes = ["text/html", "application/xhtml+xml", "application/pdf", "text/plain",
                            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"],
                        AppleUniformTypeIdentifiers = ["public.html", "com.adobe.pdf", "public.plain-text",
                            "org.openxmlformats.presentationml.presentation", "org.openxmlformats.wordprocessingml.document"],
                    },
                ],
            });
            vm.Workspace.AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppHost.Get<ILogger<HomePage>>().LogWarning(ex, "Opening the document picker failed");
            vm.Workspace.ReportMessage(vm.Workspace.PickerFailureText);
        }
        finally
        {
            _pickerOpen = false;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropZone.Classes.Remove("over");
        e.Handled = true;
        if (DataContext is not HomePageViewModel vm)
            return;
        var dragged = DraggedFiles.Read(e.DataTransfer, forDrop: true);
        if (dragged.Web is not null)
        {
            dragged.Web.Dispose();
            e.DragEffects = DragDropEffects.None;
            vm.Workspace.ReportMessage(vm.Workspace.BrowserDropText);
            return;
        }
        e.DragEffects = dragged.Files.Any(p => DocumentWorkspaceViewModel.SupportsPath(p) && File.Exists(p))
            ? DragDropEffects.Copy : DragDropEffects.None;
        vm.Workspace.AddFiles(dragged.Files);
    }
}
