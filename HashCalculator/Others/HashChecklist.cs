using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HashCalculator.ViewModels.Pages;

namespace HashCalculator;

public class HashChecker
{
    private bool fileIndependent;

    /// <summary>
    /// 已记录的条目数，即下面两个数组的有效长度。
    /// </summary>
    private int itemCount;

    /// <summary>
    /// 已记录的算法，与哈希值数组同下标一一对应（同一算法可能重复出现）。
    /// </summary>
    private AlgoType[] algoTypes = Array.Empty<AlgoType>();

    /// <summary>
    /// 已记录的哈希值，与算法数组同下标一一对应。
    /// </summary>
    private byte[][] hashByteLists = Array.Empty<byte[]>();

    internal bool IsExplicitPath { get; set; }

    /// <summary>
    /// 清单里是否存在不带文件名的条目（纯哈希列表）。这类清单的哈希值与具体行无法一一对应，
    /// 不应把期望值回填到各行的【校验值】列。
    /// </summary>
    public bool IsFileIndependent => this.fileIndependent;

    public HashChecker() { }

    public HashChecker(string relpath, AlgoType algo, byte[] hashBytes)
    {
        this.AddCheckerItem(relpath, algo, hashBytes);
    }

    public void AddCheckerItem(string relpath, AlgoType algo, byte[] hashBytes)
    {
        if (this.itemCount == this.algoTypes.Length)
        {
            int capacity = this.itemCount == 0 ? 4 : this.itemCount * 2;
            Array.Resize(ref this.algoTypes, capacity);
            Array.Resize(ref this.hashByteLists, capacity);
        }
        this.algoTypes[this.itemCount] = algo;
        this.hashByteLists[this.itemCount] = hashBytes;
        this.fileIndependent = relpath == string.Empty;
        ++this.itemCount;
    }

    public CmpRes ComparisonResultOf(AlgoType algoType, byte[] hashBytes)
    {
        if (this.itemCount == 0)
        {
            // 清单里一条可比条目都没有时不应给出"无关联"结论，
            // 否则没有输入校验值的行也会出现校验结果图标
            return CmpRes.NoResult;
        }
        if (hashBytes == null)
        {
            return CmpRes.Unrelated;
        }
        // 同一算法可能记录了多个哈希值，彼此不一致时说明清单本身不可信
        int firstIndex = -1;
        for (int i = 0; i < this.itemCount; ++i)
        {
            if (this.algoTypes[i] != algoType)
            {
                continue;
            }
            if (firstIndex < 0)
            {
                firstIndex = i;
            }
            else if (!BytesComparer.Default.Equals(this.hashByteLists[firstIndex], this.hashByteLists[i]))
            {
                return CmpRes.Uncertain;
            }
        }
        if (firstIndex < 0)
        {
            byte[] candidate = null;
            for (int i = 0; i < this.itemCount; ++i)
            {
                if (this.algoTypes[i] == AlgoType.UNKNOWN &&
                    this.hashByteLists[i].Length == hashBytes.Length)
                {
                    byte[] value = this.hashByteLists[i];
                    if (this.fileIndependent)
                    {
                        if (BytesComparer.Default.Equals(value, hashBytes))
                            return CmpRes.Matched;
                    }
                    else
                    {
                        if (candidate != null && !BytesComparer.Default.Equals(candidate, value))
                            return CmpRes.Uncertain;
                        candidate = value;
                    }
                }
            }
            return candidate == null ? CmpRes.Unrelated :
                BytesComparer.Default.Equals(candidate, hashBytes) ? CmpRes.Matched : CmpRes.Uncertain;
        }
        return BytesComparer.Default.Equals(this.hashByteLists[firstIndex], hashBytes) ?
            CmpRes.Matched : this.fileIndependent ? CmpRes.Unrelated : CmpRes.Mismatch;
    }

    public void SetComparisonResult(HashViewModel model)
    {
        if (model != null && model.AlgoInOutModels != null)
        {
            foreach (AlgoInOutModel inOut in model.AlgoInOutModels)
            {
                inOut.HashCmpResult = this.ComparisonResultOf(inOut.AlgoType, inOut.HashResult);
            }
        }
    }

    public HashSet<AlgoType> GetExistingAlgoTypes()
    {
        HashSet<AlgoType> existingAlgoTypes = new HashSet<AlgoType>();
        for (int i = 0; i < this.itemCount; ++i)
        {
            if (this.algoTypes[i] != AlgoType.UNKNOWN)
            {
                existingAlgoTypes.Add(this.algoTypes[i]);
            }
        }
        return existingAlgoTypes;
    }

    public HashSet<int> GetExistingDigestLengths()
    {
        HashSet<int> existingDigestLengths = new HashSet<int>();
        for (int i = 0; i < this.itemCount; ++i)
        {
            existingDigestLengths.Add(this.hashByteLists[i].Length);
        }
        return existingDigestLengths;
    }

    /// <summary>
    /// 把清单的期望值与比对结论一次性写入目标行。<br/>
    /// 结论一律取自 <see cref="ComparisonResultOf"/>，期望值只在该结论有明确含义时显示：
    /// 已匹配显示与结果相同的值，不匹配显示清单里的值，不确定/无关联则清空。<br/>
    /// 这样【校验值】列与【校验结果】列永远不会互相矛盾；无文件名的纯哈希清单、
    /// 同算法多条目、UNKNOWN（算法未知）回退等情形都按同一套语义处理。<br/>
    /// 仅在结果尚未算出时，才允许用清单里唯一的候选值预填，且不给出比对结论。
    /// </summary>
    public void ApplyTo(HashViewModel model, OutputType outputType)
    {
        if (model?.AlgoInOutModels == null)
        {
            return;
        }
        foreach (AlgoInOutModel inOut in model.AlgoInOutModels)
        {
            byte[] actual = inOut.HashResult;
            CmpRes result = this.ComparisonResultOf(inOut.AlgoType, actual);
            byte[] expected;
            switch (result)
            {
                case CmpRes.Matched:
                    expected = actual;
                    break;
                case CmpRes.Mismatch:
                    expected = this.FirstSameAlgoBytes(inOut.AlgoType);
                    break;
                default:
                    expected = null;
                    break;
            }
            if (actual == null && result == CmpRes.Unrelated)
            {
                // 文件还没算出结果：清单里若有唯一候选值，先填进【校验值】列
                expected = this.UniqueExpectedBytes(inOut.AlgoType);
                result = CmpRes.NoResult;
            }
            inOut.SetExpectedAndResult(expected, result, outputType);
        }
    }

    /// <summary>取清单中该算法的第一个期望值；没有同算法条目时返回 null。</summary>
    private byte[] FirstSameAlgoBytes(AlgoType algoType)
    {
        for (int i = 0; i < this.itemCount; ++i)
        {
            if (this.algoTypes[i] == algoType)
            {
                return this.hashByteLists[i];
            }
        }
        return null;
    }

    /// <summary>
    /// 结果尚未算出时用于预填：优先同算法条目且值唯一，其次 UNKNOWN 条目且值唯一。
    /// 存在多个互不相同的候选值时无法判断该显示哪个，返回 null。
    /// </summary>
    private byte[] UniqueExpectedBytes(AlgoType algoType)
    {
        byte[] unique = null;
        for (int i = 0; i < this.itemCount; ++i)
        {
            if (this.algoTypes[i] != algoType)
            {
                continue;
            }
            if (unique == null)
            {
                unique = this.hashByteLists[i];
            }
            else if (!BytesComparer.Default.Equals(unique, this.hashByteLists[i]))
            {
                return null;
            }
        }
        if (unique != null)
        {
            return unique;
        }
        for (int i = 0; i < this.itemCount; ++i)
        {
            if (this.algoTypes[i] != AlgoType.UNKNOWN)
            {
                continue;
            }
            if (unique == null)
            {
                unique = this.hashByteLists[i];
            }
            else if (!BytesComparer.Default.Equals(unique, this.hashByteLists[i]))
            {
                return null;
            }
        }
        return unique;
    }
}

public class HashChecklist : IEnumerable<KeyValuePair<string, HashChecker>>
{
    /// <summary>
    /// 每次从文件读取的字符数，同时用作 StreamReader 的内部缓冲大小。
    /// 取较大值是为了摊薄读取与解码的固定开销（StreamReader 的默认缓冲只有 1024 字符）。
    /// </summary>
    private const int ChecklistReadChars = 64 * 1024;

    /// <summary>
    /// 分块解析校验信息文件时每块累积的字符数。
    /// 块越大正则匹配次数越少，但块字符串及其构建缓冲会同时驻留，故在匹配效率与峰值内存之间取折中。
    /// </summary>
    private const int ChecklistChunkChars = 4 * 1024 * 1024;

    /// <summary>
    /// 缓冲允许等待解析的字符数上限（安全阀）。<br/>
    /// 缓冲里保留的只是"还没解析完的部分"：正常情况下只有最后一条成功匹配之后的一小段，
    /// 远达不到这个量；只有整份文件都没有换行、或某条记录本身极大的文件才会逼近它。
    /// 到顶时按"解析不了"处理并报错，避免缓冲无限增长。
    /// </summary>
    private const int ChecklistWaitChars = 32 * 1024 * 1024;

    private static Encoding[] _supportedEncodings = null;
    private static readonly char[] directorySeparators = new char[] { '/', '\\' };
    private static readonly char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
    private Dictionary<string, HashChecker> fileHashCheckerDict = null;
    private Dictionary<string, HashChecker> resolvedNames;
    private readonly Dictionary<int, AlgoType> inferredAlgorithms = new();

    /// <summary>
    /// 统一的文件哈希表访问入口：字典惰性创建，保证任何实例（包括仅经无参构造、
    /// 尚未调用 Initialize 的实例）上读取都不会出现空引用。
    /// </summary>
    private Dictionary<string, HashChecker> FileHashCheckerDict =>
        this.fileHashCheckerDict ??= new Dictionary<string, HashChecker>(
            StringComparer.OrdinalIgnoreCase);

    public static HashChecklist Text(string text)
    {
        HashChecklist checklist = new HashChecklist();
        checklist.UpdateWithText(text);
        return checklist;
    }

    public static HashChecklist File(string filePath)
    {
        HashChecklist checklist = new HashChecklist();
        checklist.UpdateWithFile(filePath);
        return checklist;
    }

    public static HashChecklist File(string filePath, IEnumerable<AlgoType> algoTypes)
    {
        HashChecklist checklist = new HashChecklist();
        checklist.AlgoTypesFromOption = algoTypes?.ToArray();
        checklist.UpdateWithFile(filePath);
        return checklist;
    }

    public static HashChecklist Checklist(HashChecklist old)
    {
        HashChecklist checklist = new HashChecklist();
        checklist.UpdateWithChecklist(old);
        return checklist;
    }

    public HashChecklist() { }

    public AlgoType AlgoTypeFromFileExt { get; set; } =
        AlgoType.UNKNOWN;

    public AlgoType[] AlgoTypesFromOption { get; set; }

    public string ReasonForFailure { get; private set; }

    internal string SourceDirectory { get; private set; }
    internal bool HasNamedEntries => this.FileHashCheckerDict.Count >
        (this.FileHashCheckerDict.ContainsKey(string.Empty) ? 1 : 0);

    IEnumerator IEnumerable.GetEnumerator()
    {
        return this.FileHashCheckerDict.GetEnumerator();
    }

    public IEnumerator<KeyValuePair<string, HashChecker>> GetEnumerator()
    {
        return this.FileHashCheckerDict.GetEnumerator();
    }

    private void Initialize(bool keepSource = false)
    {
        this.ReasonForFailure = null;
        if (!keepSource)
            this.SourceDirectory = null;
        this.resolvedNames = null;
        this.inferredAlgorithms.Clear();
        // 惰性属性保证字典存在，清空即可；不再需要单独处理"首次创建"
        this.FileHashCheckerDict.Clear();
    }

    public bool AddChecklistItem(string algo, string hash, string relpath)
    {
        if (algo == null || hash == null || relpath == null)
        {
            return false;
        }
        if (!AlgorithmsModel.TryGetAlgoType(algo, out AlgoType algoType) ||
            algoType == AlgoType.UNKNOWN)
        {
            algoType = this.AlgoTypeFromFileExt;
        }
        if (CommonUtils.HashBytesFromString(hash) is byte[] hashBytes &&
            CommonUtils.IsSupportedDigestLength(hashBytes.Length))
        {
            bool explicitPath = relpath.IndexOfAny(directorySeparators) >= 0;
            if (relpath.Length != 0 && !TryNormalizeRelativeKey(relpath, out relpath))
            {
                return false;
            }
            if (!this.FileHashCheckerDict.TryGetValue(relpath, out HashChecker hashChecker))
            {
                hashChecker = new HashChecker();
                this.FileHashCheckerDict[relpath] = hashChecker;
            }
            hashChecker.IsExplicitPath |= explicitPath;
            if (algoType == AlgoType.UNKNOWN && relpath.Length != 0)
            {
                if (!this.inferredAlgorithms.TryGetValue(hashBytes.Length, out algoType))
                {
                    algoType = AlgorithmsModel.InferChecklistAlgorithm(hashBytes.Length, this.AlgoTypesFromOption);
                    this.inferredAlgorithms.Add(hashBytes.Length, algoType);
                }
            }
            hashChecker.AddCheckerItem(relpath, algoType, hashBytes);
            return true;
        }
        return false;
    }

    private static IEnumerable<TemplateForChecklistModel> GetParsers(string extension)
    {
        if (!string.IsNullOrEmpty(extension))
        {
            foreach (TemplateForChecklistModel model in Settings.Current.TemplatesForChecklist)
            {
                if (model.ContainsExtension(extension))
                {
                    yield return model;
                }
            }
        }
        foreach (TemplateForChecklistModel template in Settings.Current.TemplatesForChecklist)
        {
            if (template.ContainsExtension(null))
            {
                yield return template;
            }
        }
    }

    private static void PrepareSupportedEncodings()
    {
        // 用于兼容 .NET Core 及以上版本，避免找不到 GB18030 等编码。
        // 注册 CodePagesEncodingProvider.Instance 后，
        // 在 Windows 上， GetEncoding(0) 返回与系统的活动代码页匹配的编码，
        // 该代码与 .NET Framework 中的行为相同。
        // Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // 上一句改为在 App.StartupHandler 方法处执行一次即可。
        _supportedEncodings = new Encoding[] {
            Encoding.GetEncoding(Encoding.UTF8.CodePage,
                    new EncoderExceptionFallback(),
                    new DecoderExceptionFallback()),
            // ANSI 兼容编码，返回值依赖于系统的活动代码页，见上注释
            Encoding.GetEncoding(0, new EncoderExceptionFallback(),
                    new DecoderExceptionFallback()),
            Encoding.GetEncoding(Encoding.Unicode.CodePage,
                    new EncoderExceptionFallback(),
                    new DecoderExceptionFallback()),
            Encoding.GetEncoding("GB18030", new EncoderExceptionFallback(),
                    new DecoderExceptionFallback()),
        };
    }

    public string UpdateWithFile(string filePath)
    {
        this.Initialize();
        this.AlgoTypeFromFileExt = AlgoType.UNKNOWN;
        try
        {
            this.SourceDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            string fileExt = Path.GetExtension(filePath);
            if (AlgorithmsModel.TryGetAlgoType(fileExt.TrimStart('.'),
                out AlgoType algoType))
            {
                this.AlgoTypeFromFileExt = algoType;
            }
            if (_supportedEncodings == null)
            {
                PrepareSupportedEncodings();
            }
            // 逐个候选编码充当探测：解码抛 DecoderFallbackException、或虽能解码但
            // 一条校验信息都解析不出来，都说明这份编码不对（例如无 BOM 的 UTF-16LE
            // 在严格 UTF-8 下只是一串合法的 NUL/ASCII，读到底也不报错），换下一个编码。
            foreach (Encoding encoding in _supportedEncodings)
            {
                using (StreamReader reader = new StreamReader(filePath, encoding, true,
                    ChecklistReadChars))
                {
                    try
                    {
                        if (this.ExtendChecklistWithReader(reader, fileExt))
                        {
                            return this.ReasonForFailure;
                        }
                        this.Initialize(keepSource: true);
                    }
                    catch (DecoderFallbackException)
                    {
                        this.Initialize(keepSource: true);
                    }
                }
            }
            this.ReasonForFailure = "无法解析校验信息文件：内容不符合任何已启用的解析方案，" +
                "或文件编码不是 UTF8/ANSI/UNICODE/GB18030 及兼容编码。";
        }
        catch (Exception ex)
        {
            this.FileHashCheckerDict.Clear();
            this.ReasonForFailure = $"出现异常导致解析校验信息失败：\n{ex.Message}";
        }
        return this.ReasonForFailure;
    }

    /// <summary>
    /// 分块读取并解析校验信息，避免整份文本常驻内存。<br/>
    /// 仅支持"匹配跨度有界"的解析方案：跨度无界的正则（如 (?s)、[\s\S]*）无法分块；
    /// 积累的未解析内容超过 <see cref="ChecklistWaitChars"/> 个字符即报错终止。
    /// </summary>
    private bool ExtendChecklistWithReader(StreamReader reader, string fileExt)
    {
        // 解析方案只取决于扩展名与设置，与文件内容无关：先判空，省掉整份文件的读取与逐块切字符串
        ChecklistParserSession parserSession = new ChecklistParserSession(fileExt);
        if (parserSession.IsEmpty)
        {
            this.ReasonForFailure = "没有可用的校验信息解析方案。";
            // 与编码无关，换哪种编码都不会有方案，直接定案
            return true;
        }
        int readCount;
        char[] readBuffer = new char[ChecklistReadChars];
        StringBuilder chunkBuilder = new StringBuilder(ChecklistChunkChars);
        // 上次解析时缓冲的长度。这里用"缓冲又攒够一块"（而不是"缓冲长度超过一块"）决定是否解析：
        // 解析时会把还没匹配上的内容留在缓冲里（见 ExtendChecklistWithChunk），缓冲会长期大于一块，
        // 若按长度判断，则每读入一小段（ChecklistReadChars）都会重新解析整段，并切出一份与缓冲等长的
        // 大字符串（4M 字符约 8 MB，会进大对象堆），白白产生大对象
        int parsedLength = 0;
        // 直接按字符块读取而不是用 ReadLine：后者会为每一行分配一个临时字符串，
        // 数十万行的文件会产生同量级的短命垃圾，加重 GC 负担
        while ((readCount = reader.Read(readBuffer, 0, readBuffer.Length)) > 0)
        {
            chunkBuilder.Append(readBuffer, 0, readCount);
            if (chunkBuilder.Length - parsedLength < ChecklistChunkChars)
            {
                continue;
            }
            if (!this.ExtendChecklistWithChunk(chunkBuilder, parserSession))
            {
                // 等待解析的内容已到上限，ReasonForFailure 已设置，与编码无关
                return true;
            }
            parsedLength = chunkBuilder.Length;   // 解析后缓冲已变短，按当前长度重新计时
        }
        if (chunkBuilder.Length != 0)
        {
            // 文件已读完，不存在"下一块"：剩余内容必须整块解析，
            // 否则被留给下一块补全的最后一行会因为没有下一块而永远得不到解析
            this.ExtendChecklistWithChunk(chunkBuilder, parserSession, isLastChunk: true);
        }
        // 一条都没解析出来，多半是编码猜错了（例如无 BOM 的 UTF-16LE 被 UTF-8 读成
        // 一串含 NUL 的字符），返回 false 让调用方换下一个编码重试
        return parserSession.HasSettled;
    }

    /// <summary>
    /// 解析缓冲中已累积的一块，把已解析完的部分移除。<br/>
    /// 非最后一块只交完整行（残行交过去可能匹配出被截断的假记录）；只消费到最后一个成功匹配的结束位置，
    /// 之后的内容留给下一块（那里可能有下一条记录的起始部分）。<br/>
    /// 最后一块（<paramref name="isLastChunk"/> 为 true）必须整块解析，否则文件最后一行永远得不到解析。<br/>
    /// 返回 false 表示等待解析的内容超过上限（失败原因已设置），读取应当中止。
    /// </summary>
    private bool ExtendChecklistWithChunk(StringBuilder chunkBuilder,
        ChecklistParserSession parserSession, bool isLastChunk = false)
    {
        // 默认整块：最后一块之后不会再读到内容，剩余内容都要解析
        int lineEnd = chunkBuilder.Length;
        if (!isLastChunk)
        {
            // 非最后一块只把"已经完整的内容"交给解析器：
            // 把还没读到换行的残行交过去，会让以 $ 结尾的解析方案在文本末尾匹配成功，
            // 产出一条被截断的假记录
            int lastNewline = -1;
            for (int i = chunkBuilder.Length - 1; i >= 0; --i)
            {
                if (chunkBuilder[i] == '\n')
                {
                    lastNewline = i;
                    break;
                }
            }
            if (lastNewline < 0)
            {
                // 整个缓冲里一个换行符都没有：无法区分"内容到此结束"和"内容被切断"，
                // 先不解析也不消费，等下一块（读到换行或读到文件末尾时再解析）
                return this.TryAccumulateMoreContent(chunkBuilder, 0);
            }
            lineEnd = lastNewline + 1;
        }
        string lines = chunkBuilder.ToString(0, lineEnd);
        // 只消费到最后一个成功匹配的结束位置：之后的内容留给下一块，
        // 那里可能有下一条记录的起始部分；一条都没匹配上时也全部留着
        int consumed = parserSession.Extend(lines, this);
        if (isLastChunk)
        {
            // 最后一块之后不会再读到内容：不管匹配到哪，剩下的都已经解析过，整块消费
            consumed = lineEnd;
        }
        else if (!this.TryAccumulateMoreContent(chunkBuilder, consumed))
        {
            return false;
        }
        chunkBuilder.Remove(0, consumed);
        return true;
    }

    /// <summary>
    /// 判断缓冲还能不能继续等待更多内容（等待量 = 缓冲长度 − 已消费位置）：超过
    /// <see cref="ChecklistWaitChars"/> 时设置失败原因并返回 false，避免缓冲无限增长。
    /// </summary>
    private bool TryAccumulateMoreContent(StringBuilder chunkBuilder, int consumed)
    {
        if (chunkBuilder.Length - consumed > ChecklistWaitChars)
        {
            // 触发原因不一定是"单行超长"：多行文件里出现一大段任何解析方案都匹配不上的内容时，
            // 这段内容同样会一直留在这里等待解析，直到超过上限
            this.ReasonForFailure =
                $"校验信息文件中有一段内容超过 {ChecklistWaitChars / (1024 * 1024)}M 字符仍未解析，" +
                "可能是超长单行，或大段内容无法被现有解析方案解析。";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 校验信息的解析方案会话：未定居前逐块尝试各个方案，定居后只用定居的方案。<br/>
    /// 一个校验信息文件只含一种格式，方案一经选定就用于余下各块，不再改变。
    /// </summary>
    private sealed class ChecklistParserSession
    {
        private readonly TemplateForChecklistModel[] parsers;
        private TemplateForChecklistModel settledParser = null;

        public ChecklistParserSession(string fileExt)
        {
            // 物化成数组：方案只需确定一次，也可以在读文件之前就判断有没有可用方案
            this.parsers = GetParsers(fileExt).ToArray();
        }

        public bool IsEmpty => this.parsers.Length == 0;

        public bool HasSettled => this.settledParser != null;

        /// <summary>
        /// 解析一段文本，返回其中已消费到的字符位置；返回 0 表示这段文本里没有可解析的内容。
        /// </summary>
        public int Extend(string lines, HashChecklist checklist)
        {
            if (this.settledParser != null)
            {
                this.settledParser.ExtendChecklistWithLines(lines, checklist, out int consumed);
                return consumed;
            }
            foreach (TemplateForChecklistModel parser in this.parsers)
            {
                if (parser.ExtendChecklistWithLines(lines, checklist, out int consumedWhenSettled))
                {
                    this.settledParser = parser;
                    return consumedWhenSettled;
                }
            }
            return 0;
        }
    }

    public string UpdateWithText(string paragraph)
    {
        bool anyPaser = false;
        bool anyItemAdded = false;
        this.Initialize();
        this.AlgoTypeFromFileExt = AlgoType.UNKNOWN;
        foreach (TemplateForChecklistModel parser in GetParsers(null))
        {
            anyPaser = true;
            if (parser.ExtendChecklistWithLines(paragraph, this, out _))
            {
                anyItemAdded = true;
                break;
            }
        }
        if (!anyPaser)
        {
            this.ReasonForFailure = "没有可用的校验信息解析方案。";
        }
        else if (!anyItemAdded)
        {
            this.ReasonForFailure = "没有解析到校验信息，请检查解析方案或输入的文本内容。";
        }
        return this.ReasonForFailure;
    }

    public string UpdateWithChecklist(HashChecklist checklist)
    {
        if (checklist == null)
        {
            this.Initialize();
            this.ReasonForFailure = "校验信息清单为空，无法复制。";
            return this.ReasonForFailure;
        }
        if (ReferenceEquals(checklist, this))
        {
            return this.ReasonForFailure;
        }
        // 沿用所有权转移语义（避免复制整份字典），同时保证：
        // 源尚未初始化过字典时，本对象得到的是空字典而不是 null
        this.fileHashCheckerDict = checklist.fileHashCheckerDict ??
            new Dictionary<string, HashChecker>(StringComparer.OrdinalIgnoreCase);
        this.AlgoTypesFromOption = checklist.AlgoTypesFromOption;
        this.AlgoTypeFromFileExt = checklist.AlgoTypeFromFileExt;
        this.ReasonForFailure = checklist.ReasonForFailure;
        this.SourceDirectory = checklist.SourceDirectory;
        this.resolvedNames = checklist.resolvedNames;
        checklist.fileHashCheckerDict = null;
        checklist.Initialize();
        return this.ReasonForFailure;
    }

    public bool TryGetFileHashChecker(string mapKey, out HashChecker checker)
    {
        checker = null;
        return TryNormalizeRelativeKey(mapKey, out string key) &&
            (this.FileHashCheckerDict.TryGetValue(key, out checker) ||
                (this.resolvedNames?.TryGetValue(key, out checker) ?? false));
    }

    internal bool TryGetFileHashChecker(string fullPath, string relativePath, out HashChecker checker)
    {
        checker = null;
        string key = this.SourceDirectory == null ? relativePath :
            Path.IsPathFullyQualified(fullPath) ? Path.GetRelativePath(this.SourceDirectory, fullPath) : null;
        return this.TryGetFileHashChecker(key, out checker);
    }

    internal bool TryGetFileOrEmptyStrHashChecker(string fullPath, string relativePath, out HashChecker checker)
    {
        return this.TryGetFileHashChecker(fullPath, relativePath, out checker) ||
            this.FileHashCheckerDict.TryGetValue(string.Empty, out checker);
    }

    private static bool TryNormalizeRelativeKey(string key, out string normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(key) || Path.IsPathRooted(key) || IsSeparator(key[^1]))
        {
            return false;
        }
        if (key.IndexOfAny(directorySeparators) < 0)
        {
            if (!IsUsablePathPart(key))
                return false;
            normalized = key;
            return true;
        }
        string[] parts = key.Split(directorySeparators, StringSplitOptions.RemoveEmptyEntries);
        int count = 0;
        foreach (string part in parts)
        {
            if (part == ".")
                continue;
            if (!IsUsablePathPart(part))
                return false;
            parts[count++] = part;
        }
        if (count == 0)
            return false;
        normalized = string.Join('\\', parts, 0, count);
        return true;
    }

    private static bool IsSeparator(char c) => c == '\\' || c == '/';

    private static bool IsUsablePathPart(string part) =>
        part[^1] != '.' && part[^1] != ' ' && part.IndexOfAny(invalidFileNameChars) < 0;

    internal List<(string Path, string Error)> ResolveFileEntries(string root,
        Func<IEnumerable<string>> enumerateFiles, CancellationToken token)
    {
        var entries = new List<(string Path, string Error)>();
        var unresolved = new Dictionary<string, (string Candidate, bool Ambiguous)>(StringComparer.OrdinalIgnoreCase);
        var resolved = new Dictionary<string, HashChecker>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, checker) in this.FileHashCheckerDict)
        {
            if (token.IsCancellationRequested)
                return entries;
            if (key.Length == 0)
                continue;
            string path = Path.Combine(root, key);
            if (System.IO.File.Exists(path))
                entries.Add((path, null));
            else if (checker.IsExplicitPath)
                entries.Add((path, "找不到清单指定的文件。"));
            else
                unresolved.Add(key, (null, false));
        }
        if (unresolved.Count != 0)
        {
            foreach (string path in enumerateFiles())
            {
                if (token.IsCancellationRequested)
                    return entries;
                string name = Path.GetFileName(path);
                if (unresolved.TryGetValue(name, out var match) && !match.Ambiguous)
                {
                    unresolved[name] = (match.Candidate ?? path, match.Candidate != null);
                }
            }
            foreach (var (key, match) in unresolved)
            {
                if (match.Ambiguous || match.Candidate == null)
                {
                    entries.Add((Path.Combine(root, key), match.Ambiguous
                        ? "有多个同名文件，请在清单中指定相对路径。" : "找不到清单指定的文件。"));
                    continue;
                }
                string relative = Path.GetRelativePath(root, match.Candidate);
                if (this.FileHashCheckerDict.ContainsKey(relative))
                {
                    entries.Add((Path.Combine(root, key), "该文件已有显式路径条目，请检查清单。"));
                    continue;
                }
                resolved.Add(relative, this.FileHashCheckerDict[key]);
                entries.Add((match.Candidate, null));
            }
        }
        this.resolvedNames = resolved;
        return entries;
    }
}
