using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BililiveRecorder.ToolBox;
using BililiveRecorder.ToolBox.Tool.Analyze;
using BililiveRecorder.ToolBox.Tool.Export;
using BililiveRecorder.ToolBox.Tool.Fix;
using BililiveRecorder.WPF.Controls;
using Microsoft.WindowsAPICodePack.Dialogs;
using Serilog;
using WPFLocalizeExtension.Extensions;

#nullable enable
namespace BililiveRecorder.WPF.Pages
{
    /// <summary>
    /// Interaction logic for ToolboxAutoFixPage.xaml
    /// </summary>
    public partial class ToolboxAutoFixPage
    {
        private static readonly ILogger logger = Log.ForContext<ToolboxAutoFixPage>();
        private readonly SemaphoreSlim semaphoreSlim = new SemaphoreSlim(1, 1);

        private readonly AutoFixSettings settings = new AutoFixSettings();

        public ToolboxAutoFixPage()
        {
            this.InitializeComponent();

            this.SettingsArea.DataContext = this.settings;
        }

        private void SelectFile_Button_Click(object sender, RoutedEventArgs e)
        {
            var title = LocExtension.GetLocalizedValue<string>("BililiveRecorder.WPF:Strings:Toolbox_AutoFix_SelectInputDialog_Title");
            var fileDialog = new CommonOpenFileDialog()
            {
                Title = title,
                IsFolderPicker = false,
                Multiselect = false,
                AllowNonFileSystemItems = false,
                AddToMostRecentlyUsedList = false,
                EnsurePathExists = true,
                EnsureFileExists = true,
                NavigateToShortcut = true,
                Filters =
                {
                    new CommonFileDialogFilter("FLV",".flv"),
                    new CommonFileDialogFilter("dev's toy",".xml,.gz,.zip")
                }
            };
            if (fileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                this.FileNameTextBox.Text = fileDialog.FileName;
            }
        }

#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void Fix_Button_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
            AutoFixProgressDialog? progressDialog = null;

            if (!this.semaphoreSlim.Wait(0))
                return;

            try
            {
                var inputPath = this.FileNameTextBox.Text;
                if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                    return;

                logger.Debug("修复文件 {Path}", inputPath);

                progressDialog = new AutoFixProgressDialog()
                {
                    CancelButtonVisibility = Visibility.Visible,
                    CancellationTokenSource = new CancellationTokenSource(),
                    Owner = Application.Current.MainWindow
                };
                var token = progressDialog.CancellationTokenSource.Token;
                var showTask = progressDialog.ShowAndDisableMinimizeToTrayAsync();

                string? output_path;
                {
                    var title = LocExtension.GetLocalizedValue<string>("BililiveRecorder.WPF:Strings:Toolbox_AutoFix_SelectOutputDialog_Title");
                    var fileDialog = new CommonSaveFileDialog()
                    {
                        Title = title,
                        AddToMostRecentlyUsedList = false,
                        EnsurePathExists = true,
                        EnsureValidNames = true,
                        NavigateToShortcut = true,
                        OverwritePrompt = false,
                        InitialDirectory = Path.GetDirectoryName(inputPath),
                        DefaultDirectory = Path.GetDirectoryName(inputPath),
                        DefaultFileName = Path.GetFileName(inputPath)
                    };
                    if (fileDialog.ShowDialog() == CommonFileDialogResult.Ok)
                        output_path = fileDialog.FileName;
                    else
                        return;
                }

                var req = new FixRequest
                {
                    Input = inputPath,
                    OutputBase = output_path,
                    PipelineSettings = new Flv.Pipeline.ProcessingPipelineSettings
                    {
                        SplitOnScriptTag = this.settings.SplitOnScriptTag,
                        DisableSplitOnH264AnnexB = this.settings.DisableSplitOnH264AnnexB,
                    }
                };

                var handler = new FixHandler();

                var resp = await handler.Handle(req, token, async p =>
                {
                    await this.Dispatcher.InvokeAsync(() =>
                    {
                        progressDialog.Progress = (int)(p * 98d);
                    });
                }).ConfigureAwait(true);

                logger.Debug("修复结果 {@Response}", resp);

                if (resp.Status != ResponseStatus.Cancelled && resp.Status != ResponseStatus.OK)
                {
                    logger.Warning(resp.Exception, "修复时发生错误 (@Status)", resp.Status);
                    await Task.Run(() => ShowErrorMessageBox(resp)).ConfigureAwait(true);
                }

                progressDialog.Hide();
                await showTask.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "修复时发生未处理的错误");
            }
            finally
            {
                try
                {
                    _ = this.Dispatcher.BeginInvoke((Action)(() => progressDialog?.Hide()));
                    progressDialog?.CancellationTokenSource?.Cancel();
                }
                catch (Exception) { }

                this.semaphoreSlim.Release();
            }
        }

        private static void ShowErrorMessageBox<T>(CommandResponse<T> resp) where T : IResponseData
        {
            var title = LocExtension.GetLocalizedValue<string>("BililiveRecorder.WPF:Strings:Toolbox_AutoFix_Error_Title");
            var type = LocExtension.GetLocalizedValue<string>("BililiveRecorder.WPF:Strings:Toolbox_AutoFix_Error_Type_" + resp.Status.ToString());
            MessageBox.Show($"{type}\n{resp.ErrorMessage}", title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void Analyze_Button_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
            AutoFixProgressDialog? progressDialog = null;

            if (!this.semaphoreSlim.Wait(0))
                return;

            try
            {
                var inputPath = this.FileNameTextBox.Text;
                if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                    return;

                logger.Debug("分析文件 {Path}", inputPath);

                progressDialog = new AutoFixProgressDialog()
                {
                    CancelButtonVisibility = Visibility.Visible,
                    CancellationTokenSource = new CancellationTokenSource(),
                    Owner = Application.Current.MainWindow
                };
                var token = progressDialog.CancellationTokenSource.Token;
                var showTask = progressDialog.ShowAndDisableMinimizeToTrayAsync();

                var req = new AnalyzeRequest
                {
                    Input = inputPath,
                    PipelineSettings = new Flv.Pipeline.ProcessingPipelineSettings
                    {
                        SplitOnScriptTag = this.settings.SplitOnScriptTag,
                        DisableSplitOnH264AnnexB = this.settings.DisableSplitOnH264AnnexB,
                    }
                };

                var handler = new AnalyzeHandler();

                var resp = await handler.Handle(req, token, async p =>
                {
                    await this.Dispatcher.InvokeAsync(() =>
                    {
                        progressDialog.Progress = (int)(p * 98d);
                    });
                }).ConfigureAwait(true);

                logger.Debug("分析结果 {@Response}", resp);

                if (resp.Status != ResponseStatus.Cancelled)
                {
                    if (resp.Status != ResponseStatus.OK)
                    {
                        logger.Warning(resp.Exception, "分析时发生错误 (@Status)", resp.Status);
                        await Task.Run(() => ShowErrorMessageBox(resp)).ConfigureAwait(true);
                    }
                    else
                    {
                        this.analyzeResultDisplayArea.DataContext = resp.Data;
                    }
                }

                progressDialog.Hide();
                await showTask.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "分析时发生未处理的错误");
            }
            finally
            {
                try
                {
                    _ = this.Dispatcher.BeginInvoke((Action)(() => progressDialog?.Hide()));
                    progressDialog?.CancellationTokenSource?.Cancel();
                }
                catch (Exception) { }

                this.semaphoreSlim.Release();
            }
        }

#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void Export_Button_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
            AutoFixProgressDialog? progressDialog = null;

            if (!this.semaphoreSlim.Wait(0))
                return;

            try
            {
                var inputPath = this.FileNameTextBox.Text;
                if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                    return;

                logger.Debug("导出文件 {Path}", inputPath);

                progressDialog = new AutoFixProgressDialog()
                {
                    CancelButtonVisibility = Visibility.Visible,
                    CancellationTokenSource = new CancellationTokenSource(),
                    Owner = Application.Current.MainWindow
                };
                var token = progressDialog.CancellationTokenSource.Token;
                var showTask = progressDialog.ShowAndDisableMinimizeToTrayAsync();

                var outputPath = string.Empty;
                {
                    var title = LocExtension.GetLocalizedValue<string>("BililiveRecorder.WPF:Strings:Toolbox_AutoFix_SelectOutputDialog_Title");
                    var fileDialog = new CommonSaveFileDialog()
                    {
                        Title = title,
                        AddToMostRecentlyUsedList = false,
                        EnsurePathExists = true,
                        EnsureValidNames = true,
                        NavigateToShortcut = true,
                        OverwritePrompt = true,
                        InitialDirectory = Path.GetDirectoryName(inputPath),
                        DefaultDirectory = Path.GetDirectoryName(inputPath),
                        DefaultFileName = Path.GetFileNameWithoutExtension(inputPath) + ".brec.xml.zip"
                    };
                    if (fileDialog.ShowDialog() == CommonFileDialogResult.Ok)
                        outputPath = fileDialog.FileName;
                    else
                        return;
                }

                var req = new ExportRequest
                {
                    Input = inputPath,
                    Output = outputPath
                };

                var handler = new ExportHandler();

                var resp = await handler.Handle(req, token, async p =>
                {
                    await this.Dispatcher.InvokeAsync(() =>
                    {
                        progressDialog.Progress = (int)(p * 95d);
                    });
                }).ConfigureAwait(true);

                logger.Debug("导出分析数据结果 {@Response}", resp);

                if (resp.Status != ResponseStatus.Cancelled && resp.Status != ResponseStatus.OK)
                {
                    logger.Warning(resp.Exception, "导出分析数据时发生错误 (@Status)", resp.Status);
                    await Task.Run(() => ShowErrorMessageBox(resp)).ConfigureAwait(true);
                }

                progressDialog.Hide();
                await showTask.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "导出时发生未处理的错误");
            }
            finally
            {
                try
                {
                    _ = this.Dispatcher.BeginInvoke((Action)(() => progressDialog?.Hide()));
                    progressDialog?.CancellationTokenSource?.Cancel();
                }
                catch (Exception) { }

                this.semaphoreSlim.Release();
            }
        }

        private void FileNameTextBox_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    this.FileNameTextBox.Text = files[0];
                }
            }
            catch (Exception)
            { }
        }

        public sealed class AutoFixSettings : INotifyPropertyChanged
        {
            private bool splitOnScriptTag;
            private bool disableSplitOnH264AnnexB;

            public event PropertyChangedEventHandler? PropertyChanged;
            private bool SetField<T>(ref T location, T value, [CallerMemberName] string propertyName = "")
            {
                if (EqualityComparer<T>.Default.Equals(location, value))
                    return false;
                location = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                return true;
            }

            /// <summary>
            /// FLV修复-检测到可能缺少数据时分段
            /// </summary>
            public bool SplitOnScriptTag { get => this.splitOnScriptTag; set => this.SetField(ref this.splitOnScriptTag, value); }

            /// <summary>
            /// FLV修复-检测到 H264 Annex-B 时禁用修复分段
            /// </summary>
            public bool DisableSplitOnH264AnnexB { get => this.disableSplitOnH264AnnexB; set => this.SetField(ref this.disableSplitOnH264AnnexB, value); }
        }
    }
}
