using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PromptVault.App.Services;

namespace PromptVault.Tests;

public sealed class ClipboardCapturePolicyTests
{
    [Fact]
    public async Task WholeFileListIsDecidedBeforeAnyFallback()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "PromptVaultClipboardPolicy", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                var first = Path.Combine(root, "first.PNG");
                var second = Path.Combine(root, "second.jpg");
                var text = Path.Combine(root, "document.txt");
                var imageNamedDirectory = Path.Combine(root, "folder.png");
                File.WriteAllText(first, "fixture");
                File.WriteAllText(second, "fixture");
                File.WriteAllText(text, "fixture");
                Directory.CreateDirectory(imageNamedDirectory);
                var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
                bitmap.Freeze();
                foreach (var files in new[] { Array.Empty<string>(), new[] { first, second }, new[] { first, text }, new[] { root }, new[] { imageNamedDirectory }, new[] { text }, new[] { Path.Combine(root, "missing.png") } })
                {
                    var data = new DataObject();
                    data.SetData(DataFormats.FileDrop, files);
                    data.SetData(DataFormats.Bitmap, bitmap);
                    data.SetData(DataFormats.UnicodeText, "must not become a prompt");
                    var feedback = 0;
                    Assert.Null(ClipboardMonitor.ReadClipboardSnapshot(data, 7, 9, () => feedback++));
                    Assert.Equal(0, feedback);
                }
                var single = new DataObject(DataFormats.FileDrop, new[] { first });
                Assert.Equal(first, ClipboardMonitor.ReadClipboardSnapshot(single, 7, 9)!.FilePath);
                var image = new DataObject(DataFormats.Bitmap, bitmap);
                Assert.True(ClipboardMonitor.ReadClipboardSnapshot(image, 7, 9)!.HasImage);
                var prompt = new DataObject(DataFormats.UnicodeText, "normal prompt");
                Assert.Equal("normal prompt", ClipboardMonitor.ReadClipboardSnapshot(prompt, 7, 9)!.Text);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        thread.Join();
    }
}
