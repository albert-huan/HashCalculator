using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;

namespace HashCalculator;

public class AlgoInOutModel : BaseViewModel
{
    private CmpRes _hashCmpResult;
    private byte[] _hashResult;
    private string _expectedHashString = null;
    private byte[] _expectedBytes = null;
    private bool _expectedFromChecklist = false;
    private bool _export = false;
    private bool _selected = false;
    private string _algorithmAlias = null;
    private string[] _aliasWords = null;
    private readonly string _defaultAlias = null;

    /// <summary>
    /// 本模型自己的计算实例，含计算状态：按需创建，一轮计算结束由
    /// <see cref="ReleaseAlgo"/> 释放并丢弃，下次需要时重新创建。
    /// </summary>
    private IHashAlgoInfo _algoInstance;

    /// <summary>
    /// 共享的算法信息：同一算法的所有模型（模板与各行的克隆）共用同一个实例，
    /// 只用于读取元数据（AlgoName/AlgoType/DigestLength）与创建计算实例，不含计算状态。
    /// </summary>
    private readonly IHashAlgoInfo _algoInfo;

    private static readonly string[] separators = new string[]
    {
        ",",
        "\n",
        "\r\n",
    };

    private RelayCommand _copyHashResultCmd;

    public AlgoInOutModel(IHashAlgoInfo algoInfo, string defaultAlias) :
        this(algoInfo, defaultAlias, alias: null)
    {
    }

    public AlgoInOutModel(IHashAlgoInfo algoInfo, string defaultAlias, string alias)
    {
        this._algoInfo = algoInfo;
        this.AlgoName = algoInfo.AlgoName;
        this.AlgoType = algoInfo.AlgoType;
        this._defaultAlias = defaultAlias;
        this.AlgorithmAlias = alias ?? defaultAlias;
    }

    public string AlgoName { get; }

    public AlgoType AlgoType { get; }

    /// <summary>
    /// 共享的算法信息，供读取元数据与创建计算实例使用。
    /// </summary>
    public IHashAlgoInfo IAlgo => this._algoInfo;

    /// <summary>
    /// 本模型的计算实例，按需创建。一轮计算结束后由 <see cref="ReleaseAlgo"/> 置空，
    /// 所以不要跨轮长期持有它，需要时重新读取本属性即可。
    /// </summary>
    public HashAlgorithm Algo => (HashAlgorithm)(this._algoInstance ??= this._algoInfo.NewInstance());

    /// <summary>
    /// 释放计算实例的非托管状态并丢弃实例，重算时会重新创建。<br/>
    /// 不调用 HashAlgorithm.Dispose：它会让实例永久不可用，而这里只需要释放状态。
    /// </summary>
    public void ReleaseAlgo()
    {
        this._algoInstance?.Release();
        this._algoInstance = null;
    }

    public bool Export
    {
        get => this._export;
        set => this.SetPropNotify(ref this._export, value);
    }

    public bool Selected
    {
        get => this._selected;
        set
        {
            // AlgoGroupModel 类根据此属性的变化计数
            // 所以新值与旧值没有区别则不能触发通知，否则计数就不正确
            if (value != this._selected)
            {
                this.SetPropNotify(ref this._selected, value);
            }
        }
    }

    public byte[] HashResult
    {
        get => this._hashResult;
        set
        {
            if (this._hashResult == value)
            {
                return;
            }
            this._hashResult = value;
            this.NotifyPropertyChanged();
            // 不在此处比对：计算结束后由 HashViewModel.ApplyHashCmpResult 统一按
            // 清单语义或手填值语义判定，避免中途用二元结论覆盖清单的判定结果
        }
    }

    /// <summary>
    /// 用户手动填入（或由校验信息清单回填）的期望哈希值。<br/>
    /// 与 <see cref="HashResult"/> 同时存在时实时比对并更新 <see cref="HashCmpResult"/>，
    /// 使校验结果列在填写/修改后立即反映匹配情况。
    /// </summary>
    public string ExpectedHashString
    {
        get => this._expectedHashString;
        set
        {
            if (this._expectedHashString == value)
            {
                return;
            }
            this._expectedHashString = value;
            // 用户手填的值保持原样：不随输出方式重排，也不被清单回填的字节覆盖
            this._expectedBytes = null;
            this._expectedFromChecklist = false;
            this.NotifyPropertyChanged();
            if (string.IsNullOrWhiteSpace(value))
            {
                // 用户清空了本行期望值，对应的校验结果也应清除
                this.HashCmpResult = CmpRes.NoResult;
            }
            else
            {
                this.RefreshCmpResultFromExpected();
            }
        }
    }

    /// <summary>
    /// 用期望哈希值与已算出的结果重新比对，刷新校验结果。<br/>
    /// 期望值为空或结果尚未算出时不改动既有结果（可能来自清单判定），
    /// 初始态本身就是 <see cref="CmpRes.NoResult"/>。
    /// </summary>
    public void RefreshCmpResultFromExpected()
    {
        if (string.IsNullOrWhiteSpace(this._expectedHashString) || this.HashResult == null)
        {
            return;
        }
        byte[] expected = CommonUtils.HashBytesFromString(
            this._expectedHashString, this.HashResult.Length);
        this.HashCmpResult = expected != null &&
            BytesComparer.Default.Equals(expected, this.HashResult) ?
            CmpRes.Matched : CmpRes.Mismatch;
    }

    /// <summary>
    /// 由校验清单写入期望值与比对结论：按当前输出方式格式化显示文本，
    /// 不触发手填值的二元比对（结论已由 HashChecker 判定）。
    /// </summary>
    internal void SetExpectedAndResult(byte[] expected, CmpRes result, OutputType outputType)
    {
        this._expectedBytes = expected;
        this._expectedFromChecklist = expected != null;
        string text = expected == null ? null :
            BytesToStrByOutputTypeCvt.Convert(expected, outputType);
        if (this._expectedHashString != text)
        {
            this._expectedHashString = text;
            this.NotifyPropertyChanged();
        }
        this.HashCmpResult = result;
    }

    /// <summary>
    /// 输出方式改变后，只重排由清单回填的期望值，用户手填的内容保持原样，
    /// 避免正在输入的文本被反复改写。
    /// </summary>
    internal void RefreshExpectedStringFormat(OutputType outputType)
    {
        if (!this._expectedFromChecklist || this._expectedBytes == null)
        {
            return;
        }
        string text = BytesToStrByOutputTypeCvt.Convert(this._expectedBytes, outputType);
        if (this._expectedHashString != text)
        {
            this._expectedHashString = text;
            this.NotifyPropertyChanged();
        }
    }

    public CmpRes HashCmpResult
    {
        get => this._hashCmpResult;
        set => this.SetPropNotify(ref this._hashCmpResult, value);
    }

    public string AlgorithmAlias
    {
        get => this._algorithmAlias;
        set
        {
            // 值未变则不通知；值变了要作废分词缓存
            if (this.AlgorithmAlias?.Equals(value) != true)
            {
                this._algorithmAlias = value;
                this._aliasWords = null;
                this.NotifyPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 别名的分词结果（按逗号/换行拆分并去除首尾空白），供匹配用，只在第一次被读取时拆分并缓存。<br/>
    /// 之所以按需计算：别名匹配只服务于"把算法名解析成 AlgoType"这一件事，而该事只在
    /// AlgorithmsModel.ProvidedAlgos 上展开；表格里每个文件对应的行模型数量可达数十万，从不读取本属性。
    /// 若在构造时为每个实例都预备一份分词结果（数组 + 懒迭代器 + 属性变更订阅），会有几十 MB 的无谓占用。
    /// </summary>
    private string[] AliasWords
    {
        get
        {
            return this._aliasWords ??= string.IsNullOrEmpty(this._algorithmAlias) ?
                Array.Empty<string>() :
                this._algorithmAlias.Split(separators, StringSplitOptions.RemoveEmptyEntries)
                    .Select(i => i.Trim()).ToArray();
        }
    }

    /// <summary>
    /// 由本模型克隆出一个新模型（表格里每个文件的行模型都由模板克隆而来）。<br/>
    /// 只共享算法信息，不预先创建计算实例：实例在真正要计算时才创建，算完即弃。
    /// </summary>
    public AlgoInOutModel NewAlgoInOutModel()
    {
        // 构造签名为 (algoInfo, defaultAlias, alias)：克隆继承模板当前的别名，
        // 默认别名仍是原始默认值，ResetAlias 才能重置回默认别名
        return new AlgoInOutModel(this._algoInfo, this._defaultAlias, this.AlgorithmAlias);
    }

    /// <summary>
    /// 指定名称是否为本算法的别名之一。一个算法可以有多个用逗号/换行分隔的等价别名。
    /// </summary>
    public bool IsMyAliasWord(string alias, StringComparer comparer)
    {
        string[] words = this.AliasWords;
        for (int i = 0; i < words.Length; ++i)
        {
            if (comparer.Equals(words[i], alias))
            {
                return true;
            }
        }
        return false;
    }

    public void ResetAlias()
    {
        this.AlgorithmAlias = this._defaultAlias;
    }

    private void CopyHashResultAction(object param)
    {
        if (this.HashResult != null && param is HashViewModel parent)
        {
            string format = Settings.Current.GenerateTextInFormat ?
                Settings.Current.FormatForGenerateText : null;
            if (this.GenerateTextInFormat(
                parent, format, parent.SelectedOutputType, endLine: false, seeExport: false,
                Settings.Current.CaseOfCopiedAlgNameFollowsOutputType) is string text)
            {
                CommonUtils.ClipboardSetText(text);
                NotificationSender.SnackbarSuccess($"已按模板复制当前哈希值：{text}");
            }
        }
    }

    public ICommand CopyHashResultCmd
    {
        get
        {
            this._copyHashResultCmd ??= new RelayCommand(this.CopyHashResultAction);
            return this._copyHashResultCmd;
        }
    }

    /// <summary>CSV 字段转义：包双引号、双引号翻倍，并阻止表格软件把值当公式执行。</summary>
    private static string EscapeCsvValue(string value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && (value[0] == '=' || value[0] == '+' ||
            value[0] == '-' || value[0] == '@' || value[0] == '\t' || value[0] == '\r'))
        {
            value = "'" + value;
        }
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>导出方案与 Word 表格共用的校验结果显示文本：一致 / 不一致 等。</summary>
    internal static string CmpResultToExportText(CmpRes result)
    {
        return result switch
        {
            CmpRes.Matched => "一致",
            CmpRes.Mismatch => "不一致",
            CmpRes.Uncertain => "不确定",
            CmpRes.Unrelated => "无关联",
            _ => "未校验",
        };
    }

    /// <summary>
    /// 参数 format 为 null 或空字符串代表不按格式生成字符串。<br/>
    /// 参数 output 为 OutputType.Unknown 代表按 parent.SelectedOutputType 格式化哈希值，<br/>
    /// 如果 parent.SelectedOutputType 也是 OutputType.Unknown，则使用 Settings.Current.SelectedOutputType。
    /// </summary>
    internal string GenerateTextInFormat(HashViewModel parent, string format, OutputType output,
        bool endLine, bool seeExport, bool casedAlgName, bool escapeCsv = false)
    {
        if (parent != null && !parent.Arguments.IsInvalidName && this.HashResult != null &&
            (!seeExport || this.Export))
        {
            if (output == OutputType.Unknown)
            {
                if (parent.SelectedOutputType != OutputType.Unknown)
                {
                    output = parent.SelectedOutputType;
                }
                else
                {
                    output = Settings.Current.SelectedOutputType;
                }
            }
            string lineBreak = Settings.Current.UseUnixStyleLineBreaks ? "\n" : "\r\n";
            if (string.IsNullOrEmpty(format))
            {
                string text = BytesToStrByOutputTypeCvt.Convert(this.HashResult, output);
                return endLine ? $"{text}{lineBreak}" : text;
            }
            else
            {
                string algoName = this.AlgoName;
                if (casedAlgName)
                {
                    algoName = output switch
                    {
                        OutputType.BinaryLower => algoName.ToLower(),
                        _ => algoName.ToUpper(),
                    };
                }
                // 一次性扫描替换：串行的 StringBuilder.Replace 会把文件名/路径里
                // 字面的 $path$ 等占位符当成模板再替换一次
                string CsvField(string value) => escapeCsv ? EscapeCsvValue(value) : value;
                string text = System.Text.RegularExpressions.Regex.Replace(format,
                    "\\$(?:horztab|newline|algo|hash|name|path|relpath|filesize|expected|cmp)\\$",
                    match => match.Value switch
                    {
                        "$horztab$" => "\t",
                        "$newline$" => lineBreak,
                        "$algo$" => CsvField(algoName),
                        "$hash$" => CsvField(BytesToStrByOutputTypeCvt.Convert(this.HashResult, output)),
                        "$name$" => CsvField(parent.Information.Name),
                        "$path$" => CsvField(parent.Arguments.Deprecated ?
                            parent.Information.Name : parent.Information.FullName),
                        "$relpath$" => CsvField(parent.RelativePath),
                        "$filesize$" => CsvField(parent.FileLength.ToString()),
                        "$expected$" => CsvField(this.ExpectedHashString ?? string.Empty),
                        "$cmp$" => CsvField(CmpResultToExportText(this.HashCmpResult)),
                        _ => match.Value,
                    });
                return endLine ? text + lineBreak : text;
            }
        }
        return default(string);
    }
}
