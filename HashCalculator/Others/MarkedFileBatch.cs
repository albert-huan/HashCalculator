using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HashCalculator.ViewModels.Windows;
using HashCalculator.Views.Windows;

namespace HashCalculator;

internal static class MarkedFileBatch
{
    internal static void WriteCopy(string directory, string name, Action<Stream> write, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string temporary = Path.Combine(directory, $".hashcalculator-{Guid.NewGuid():N}.tmp");
        bool ownsTemporary = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                ownsTemporary = true;
                write(output);
            }
            string stem = Path.GetFileNameWithoutExtension(name);
            string extension = Path.GetExtension(name);
            for (int suffix = 0; ; suffix++)
            {
                token.ThrowIfCancellationRequested();
                string target = Path.Combine(directory, suffix == 0 ? name : $"{stem}_{suffix}{extension}");
                try
                {
                    File.Move(temporary, target, overwrite: false);
                    ownsTemporary = false;
                    return;
                }
                catch (IOException) when (File.Exists(target) || Directory.Exists(target))
                {
                    // The target may have been created while the temporary file was being written.
                }
                catch (UnauthorizedAccessException) when (Directory.Exists(target))
                {
                }
            }
        }
        finally
        {
            if (ownsTemporary)
                File.Delete(temporary);
        }
    }

    internal static async Task<string> RunAsync(HashViewModel[] models, ProgressWindow window,
        ProgressWindowModel progress, Func<HashViewModel, string> process)
    {
        progress.TotalCount = models.Length;
        CancellationToken token = progress.TokenSrc.Token;
        try
        {
            return await Task.Run(() =>
            {
                int succeeded = 0, skipped = 0, failed = 0;
                var details = new List<string>();
                foreach (HashViewModel model in models)
                {
                    if (token.IsCancellationRequested)
                        break;
                    progress.CurrentValue = 0;
                    progress.CurrentString = model.FileName;
                    try
                    {
                        string reason = process(model);
                        if (reason == null)
                            succeeded++;
                        else
                        {
                            skipped++;
                            AddDetail(model, $"跳过：{reason}");
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        failed++;
                        AddDetail(model, $"失败：{e.Message}");
                    }
                    progress.ProcessedCount++;
                }
                int remaining = models.Length - succeeded - skipped - failed;
                string summary = $"成功 {succeeded} 个，跳过 {skipped} 个，失败 {failed} 个。";
                if (remaining > 0)
                    summary += $" 已取消，{remaining} 个未完成。";
                if (skipped + failed > details.Count)
                    details.Add($"另有 {skipped + failed - details.Count} 条。");
                return details.Count == 0 ? summary : summary + "\n\n" + string.Join("\n", details);

                void AddDetail(HashViewModel model, string message)
                {
                    if (details.Count < 10)
                        details.Add($"{model.Information.FullName}：{message}");
                }
            });
        }
        finally
        {
            progress.AutoClose = true;
            window.DialogResult = false;
        }
    }
}
