using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace HashCalculator
{
    public class PathPackage : IEnumerable<HashModelArg>
    {
        private static readonly string recyclebin = "$Recycle.Bin";
        private static readonly char[] invalidFnameChars = Path.GetInvalidFileNameChars();

        private readonly string[] paths = null;
        private readonly SearchMethod searchMethod;
        private readonly HashChecklist hashChecklist = null;
        private readonly string parentDir = null;

        public CancellationToken StopSearchingToken { get; set; }

        public List<AlgoType> PresetAlgoTypes { get; set; }

        public bool OnlyFilesThatExistInChecklist { get; set; } = true;

        public PathPackage(string parent, string path, SearchMethod method) :
            this(parent, new string[] { path }, checklist: null, method)
        {
        }

        public PathPackage(string parent, IEnumerable<string> paths, SearchMethod method) :
            this(parent, paths, checklist: null, method)
        {
        }

        public PathPackage(string parent, string path, HashChecklist checklist, SearchMethod method) :
             this(parent, paths: new string[] { path }, checklist, method)
        {
        }

        public PathPackage(string parent, IEnumerable<string> paths, HashChecklist checklist, SearchMethod method)
        {
            this.parentDir = parent;
            this.paths = paths is string[] array ? array : paths.ToArray();
            this.searchMethod = method;
            this.hashChecklist = checklist;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.EnumerateModelArgs();
        }

        public IEnumerator<HashModelArg> GetEnumerator()
        {
            return this.EnumerateModelArgs();
        }

        private IEnumerable<string> EnumerateFiles(string root)
        {
            switch (this.searchMethod)
            {
                default:
                case SearchMethod.Children:
                    foreach (string file in this.SafeEnumerateFiles(root))
                    {
                        yield return file;
                    }
                    break;
                case SearchMethod.Descendants:
                    bool isPartitionRoot = root.EndsWith(":\\");
                    Stack<string> directoryExplorer = new Stack<string>();
                    directoryExplorer.Push(root);
                    while (directoryExplorer.Count != 0)
                    {
                        string currentDirectory = directoryExplorer.Pop();
                        string[] subDirectories;
                        try
                        {
                            subDirectories = Directory.GetDirectories(currentDirectory);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        // Stack 后进先出的特性符合遍历文件的深度优先原则，但这个特性
                        // 也会造成以倒序的方式遍历同级的目录，所以要翻转同级目录入栈顺序
                        for (int i = subDirectories.Length - 1; i >= 0; --i)
                        {
                            string directory = subDirectories[i];
                            // 目录 junction / 符号链接可能形成环，导致无限递归和重复计算
                            if (IsReparsePoint(directory))
                            {
                                continue;
                            }
                            if (!isPartitionRoot || !Path.GetFileName(directory).Equals(recyclebin,
                                StringComparison.OrdinalIgnoreCase))
                            {
                                directoryExplorer.Push(directory);
                            }
                        }
                        foreach (string fileInCurrentDirectory in this.SafeEnumerateFiles(currentDirectory))
                        {
                            yield return fileInCurrentDirectory;
                        }
                    }
                    break;
                case SearchMethod.DontSearch:
                    break;
            }
        }

        /// <summary>
        /// Directory.EnumerateFiles 是延迟枚举，异常发生在 MoveNext 而不是创建时。
        /// 逐次迭代包在 try 里，避免单个目录被删除/权限不足就让整批搜索中断。
        /// </summary>
        private IEnumerable<string> SafeEnumerateFiles(string root)
        {
            IEnumerator<string> enumerator = null;
            try
            {
                enumerator = Directory.EnumerateFiles(root).GetEnumerator();
            }
            catch (Exception)
            {
                enumerator = null;
            }
            if (enumerator == null)
            {
                yield break;
            }
            try
            {
                while (true)
                {
                    string current = null;
                    bool failed = false;
                    try
                    {
                        if (!enumerator.MoveNext())
                        {
                            break;
                        }
                        current = enumerator.Current;
                    }
                    catch (Exception)
                    {
                        failed = true;
                    }
                    if (failed)
                    {
                        yield break;
                    }
                    yield return current;
                }
            }
            finally
            {
                enumerator.Dispose();
            }
        }

        private static bool IsReparsePoint(string directory)
        {
            try
            {
                return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception)
            {
                // 属性都取不到的目录一律跳过，避免后续枚举再抛异常
                return true;
            }
        }

        private IEnumerator<HashModelArg> EnumerateModelArgs()
        {
            if (this.hashChecklist == null)
            {
                foreach (string path in this.paths ?? Array.Empty<string>())
                {
                    if (this.StopSearchingToken.IsCancellationRequested)
                    {
                        yield break;
                    }
                    if (File.Exists(path))
                    {
                        yield return new HashModelArg(this.parentDir, path, this.PresetAlgoTypes,
                            this.hashChecklist);
                    }
                    else if (Directory.Exists(path))
                    {
                        foreach (string fileFullPath in this.EnumerateFiles(path))
                        {
                            if (this.StopSearchingToken.IsCancellationRequested)
                            {
                                yield break;
                            }
                            yield return new HashModelArg(this.parentDir, fileFullPath, this.PresetAlgoTypes,
                                this.hashChecklist);
                        }
                    }
                }
            }
            else
            {
                if (this.hashChecklist.AssertRelativeGetFull(this.parentDir,
                    out IEnumerable<string> fullPaths))
                {
                    foreach (string path in fullPaths)
                    {
                        if (this.StopSearchingToken.IsCancellationRequested)
                        {
                            yield break;
                        }
                        HashModelArg argument = new HashModelArg(this.parentDir, path,
                            this.PresetAlgoTypes, this.hashChecklist);
                        if (!File.Exists(path))
                        {
                            argument.Deprecated = true;
                            argument.Message = "找不到文件，可能校验信息文件与此文件相对位置不正确";
                        }
                        yield return argument;
                    }
                }
                else
                {
                    foreach (string path in this.paths ?? Array.Empty<string>())
                    {
                        if (this.StopSearchingToken.IsCancellationRequested)
                        {
                            yield break;
                        }
                        if (File.Exists(path))
                        {
                            yield return new HashModelArg(this.parentDir, path, this.PresetAlgoTypes,
                                this.hashChecklist);
                        }
                        else if (Directory.Exists(path))
                        {
                            foreach (string fileFullPath in this.EnumerateFiles(path))
                            {
                                if (this.StopSearchingToken.IsCancellationRequested)
                                {
                                    yield break;
                                }
                                if (!this.hashChecklist.IsNameInChecklist(Path.GetFileName(fileFullPath)) &&
                                    this.OnlyFilesThatExistInChecklist)
                                {
                                    continue;
                                }
                                yield return new HashModelArg(this.parentDir, fileFullPath,
                                    this.PresetAlgoTypes, this.hashChecklist);
                            }
                            foreach (KeyValuePair<string, HashChecker> pair in this.hashChecklist)
                            {
                                if (this.StopSearchingToken.IsCancellationRequested)
                                {
                                    yield break;
                                }
                                if (string.IsNullOrEmpty(pair.Key) || pair.Key.IndexOfAny(invalidFnameChars) != -1)
                                {
                                    yield return new HashModelArg(this.PresetAlgoTypes);
                                }
                                // 此属性由 HashChecklist.IsNameInChecklist 方法更改
                                else if (!pair.Value.IsExistingFile)
                                {
                                    yield return new HashModelArg(pair.Key, this.PresetAlgoTypes);
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
