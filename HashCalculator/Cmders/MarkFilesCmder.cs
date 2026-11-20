using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using HashCalculator.Others;
using HashCalculator.ViewModels.Windows;
using HashCalculator.Views.Windows;
using Microsoft.Win32;
using Wpfctrls = Wpf.Ui.Controls;

namespace HashCalculator;

internal class MarkFilesCmder : AbsHashesCmder
{
    private RelayCommand selectFolderCmd;
    private RelayCommand generateMarkedFilesCmd;
    private EditFileOption markFilesOption;
    private string directoryUsedToSaveFiles;

    public override ContentControl UserInterface { get; }

    public override string Display => "添加标记改变文件的哈希值";

    public override string Description => "给文件添加哈希标记以改变其哈希值，部分文件可正常使用，一般用于在某些" +
        "情况下避过相同文件检测。\n对于改变哈希值后不能正常使用的文件，用〈还原被改变哈希值的文件〉对其进行还原即可得到原文件。";

    public EditFileOption MarkFilesOption
    {
        get => this.markFilesOption;
        set => this.SetPropNotify(ref this.markFilesOption, value);
    }

    public string DirectoryUsedToSaveFiles
    {
        get => this.directoryUsedToSaveFiles;
        set => this.SetPropNotify(ref this.directoryUsedToSaveFiles, value);
    }

    public bool CheckIfUsingDistinctFilesFilter { get; set; } = true;

    public MarkFilesCmder() : this(HashModelStore.HashViewModels)
    {
    }

    public MarkFilesCmder(IEnumerable<HashViewModel> models) : base(models)
    {
        this.UserInterface = new MarkFilesCmderCtrl(this);
    }

    private void SelectFolderAction(object param)
    {
        OpenFolderDialog openFolder = new OpenFolderDialog()
        {
            InitialDirectory = Settings.Current.LastUsedPath,
        };
        if (openFolder.ShowDialog() == true)
        {
            this.DirectoryUsedToSaveFiles = openFolder.FolderName;
            Settings.Current.LastUsedPath = openFolder.FolderName;
        }
    }

    public ICommand SelectFolderCmd
    {
        get
        {
            this.selectFolderCmd ??= new RelayCommand(this.SelectFolderAction);
            return this.selectFolderCmd;
        }
    }

    private Task<string> GenerateMarkedFiles(HashViewModel[] models,
        ProgressWindow doubleProgressWindow, ProgressWindowModel doubleProgressModel)
    {
        var option = this.MarkFilesOption;
        string destination = this.DirectoryUsedToSaveFiles;
        var token = doubleProgressModel.TokenSrc.Token;
        return MarkedFileBatch.RunAsync(models, doubleProgressWindow, doubleProgressModel, model =>
        {
            if (model.Result != HashResult.NoResult && model.Result != HashResult.Succeeded)
                return "文件任务未成功。";
            var algorithm = model.CurrentInOutModel;
            if (!HcmDataHelper.AvailableInOutModel(algorithm))
                return "当前算法没有可用的哈希值。";
            if (option == EditFileOption.OriginalFile)
            {
                using FileStream stream = model.Information.Open(FileMode.Open, FileAccess.Write);
                token.ThrowIfCancellationRequested();
                long originalLength = stream.Length;
                if (!new HcmDataHelper(stream).GenerateMarkedFile(algorithm))
                {
                    if (stream.Length != originalLength)
                        stream.SetLength(originalLength);
                    throw new IOException("无法写入哈希标记。");
                }
                return null;
            }
            using FileStream source = model.Information.OpenRead();
            var helper = new HcmDataHelper(source);
            string directory = option == EditFileOption.NewInNewLocation ? destination : model.Information.DirectoryName;
            MarkedFileBatch.WriteCopy(directory, model.FileName, output =>
            {
                if (!helper.GenerateMarkedFile(output, algorithm, doubleProgressModel))
                    throw new IOException("无法生成带标记的副本。");
            }, token);
            return null;
        });
    }

    private async void GenerateMarkedFilesAction(object param)
    {
        if (Settings.Current.IsFiltersAndCmdersIdle &&
            this.RefModels is IEnumerable<HashViewModel> hashViewModels)
        {
            Settings.Current.IsFiltersAndCmdersIdle = false;
            if (this.MarkFilesOption == EditFileOption.NewInNewLocation)
            {
                if (string.IsNullOrEmpty(this.DirectoryUsedToSaveFiles) ||
                    !Path.IsPathRooted(this.DirectoryUsedToSaveFiles))
                {
                    NotificationSender.ShowMessageBox(
                        MainWindow.Current, "提示", "请输入生成的新文件的保存目录的完整路径！");
                    goto FinishingTouches;
                }
                if (!Directory.Exists(this.DirectoryUsedToSaveFiles))
                {
                    try
                    {
                        Directory.CreateDirectory(this.DirectoryUsedToSaveFiles);
                    }
                    catch (Exception)
                    {
                        NotificationSender.ShowMessageBox(
                            MainWindow.Current, "错误", "用于保存生成的新文件的目录不存在且创建失败！");
                        goto FinishingTouches;
                    }
                }
            }
            if (this.CheckIfUsingDistinctFilesFilter && !hashViewModels.Where(
                i => i.Matched).All(i => i.FileIndex != null))
            {
                if (NotificationSender.ShowMessageBox(
                    MainWindow.Current,
                    "提示",
                    "没有应用〈有效的文件〉筛选器，要继续操作吗？",
                    closeButtonText: "否",
                    primaryButtonText: "是") != Wpfctrls.ContentDialogResult.Primary)
                {
                    goto FinishingTouches;
                }
            }
            if (hashViewModels.Any(i => i.IsExecutionTarget))
            {
                HashViewModel[] targets = hashViewModels.Where(i => i.IsExecutionTarget).ToArray();
                ProgressWindowModel progressModel = new ProgressWindowModel()
                {
                    WindowTitle = "正在写入...",
                };
                ProgressWindow progressWindow = new ProgressWindow(progressModel)
                {
                    Owner = MainWindow.Current
                };
                Task<string> genMarkedFilesTask = this.GenerateMarkedFiles(targets, progressWindow, progressModel);
                progressWindow.ShowDialog();
                NotificationSender.ShowMessageBox(MainWindow.Current, "添加标记结果", await genMarkedFilesTask);
            }
            else
            {
                NotificationSender.ShowMessageBox(
                    MainWindow.Current, "提示", "没有找到任何操作目标，请选择操作目标！");
            }
        FinishingTouches:
            Settings.Current.IsFiltersAndCmdersIdle = true;
            Settings.Current.IsMainRowSelectedByCheckBox = false;
        }
    }

    public ICommand GenerateMarkedFilesCmd
    {
        get
        {
            this.generateMarkedFilesCmd ??= new RelayCommand(this.GenerateMarkedFilesAction);
            return this.generateMarkedFilesCmd;
        }
    }

    public static GenericItemModel[] AvailableMarkFilesOptions { get; } = new GenericItemModel[]
    {
        new GenericItemModel("直接把哈希标记写入到原文件上", EditFileOption.OriginalFile),
        new GenericItemModel("在原文件所在目录创建副本并写入标记", EditFileOption.NewInSameLocation),
        new GenericItemModel("在以下目录创建副本并写入标记", EditFileOption.NewInNewLocation),
    };
}
