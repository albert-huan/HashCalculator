using System.IO;
using System.Text;

namespace HashCalculator
{
    public class TemplateForExportModel
    {
        private string extension;
        private static readonly char[] invalidChars = Path.GetInvalidFileNameChars();

        public static readonly TemplateForExportModel AllModel =
            new TemplateForExportModel(
                "所有文件",
                null,
                "$hash$ *$relpath$");

        public static readonly TemplateForExportModel CsvModel =
            new TemplateForExportModel(
                "CSV文件",
                ".csv",
                "$algo$,$hash$,$relpath$")
            {
                UsingEncoding = EncodingEnum.UTF_8_BOM,
                // 引号与转义由生成时按字段值添加，模板本身保持不含引号，
                // 这样文件名里含逗号、双引号时也不会把列打乱
                EscapeCsvValues = true,
            };

        public static readonly TemplateForExportModel HcbModel =
            new TemplateForExportModel(
                "校验信息",
                ".hcb",
                "#$algo$ *$hash$ *$relpath$");

        public static readonly TemplateForExportModel SfvModel =
            new TemplateForExportModel(
                "SFV文件",
                ".sfv",
                "$relpath$ $hash$");

        public static readonly TemplateForExportModel TxtModel =
            new TemplateForExportModel(
                "文本文件",
                ".txt",
                "#$algo$ *$hash$ *$relpath$");

        /// <summary>
        /// 每行按【文件名&lt;制表符&gt;哈希值】导出，即 hashsums.txt 风格的校验清单。<br/>
        /// 该格式可被校验信息解析方案「通用三」($name$ 空白 $hash$) 原样读回再校验。
        /// </summary>
        public static readonly TemplateForExportModel NameHashTabModel =
            new TemplateForExportModel(
                "文件名+制表符+哈希值",
                ".txt",
                "$name$$horztab$$hash$");

        /// <summary>
        /// 每行按【文件名&lt;TAB&gt;算法&lt;TAB&gt;校验值&lt;TAB&gt;校验结果】导出。<br/>
        /// 校验结果以「一致 / 不一致」显示，其余状态为 不确定 / 无关联 / 未校验，
        /// 便于把校验结论连同用户填写（或清单回填）的期望值一起归档。
        /// </summary>
        public static readonly TemplateForExportModel ExpectedAndCmpModel =
            new TemplateForExportModel(
                "校验值与校验结果",
                ".txt",
                "$name$$horztab$$algo$$horztab$$expected$$horztab$$cmp$");

        public TemplateForExportModel() { }

        public TemplateForExportModel(string name, string extension, string template)
        {
            this.Name = name;
            this.Extension = extension;
            this.Template = template;
        }

        public string Name { get; set; }

        public string Extension
        {
            get
            {
                return string.IsNullOrEmpty(this.extension) ? string.Empty :
                    this.extension[0] == '.' ? this.extension : $".{this.extension}";
            }
            set
            {
                this.extension = value;
            }
        }

        public string Template { get; set; }

        public EncodingEnum UsingEncoding { get; set; }

        /// <summary>
        /// 生成时把各字段按 CSV 规则加引号并转义：支持含逗号/双引号的文件名，
        /// 并给 = + - @ 开头的字段加前缀，防止表格软件把内容当公式执行。
        /// </summary>
        public bool EscapeCsvValues { get; set; }

        public string GetFilterFormat(bool noExt)
        {
            if (this.IsValidProps())
            {
                if (noExt || string.IsNullOrEmpty(this.Extension))
                {
                    return $"{this.Name}|*.*";
                }
                else if (this.Extension.IndexOfAny(invalidChars) == -1)
                {
                    return $"{this.Name}|*{this.Extension}";
                }
            }
            return default(string);
        }

        public bool IsValidProps()
        {
            return !string.IsNullOrEmpty(this.Name) && !string.IsNullOrEmpty(this.Template);
        }

        public TemplateForExportModel Copy(string nameSuffix)
        {
            if (string.IsNullOrEmpty(nameSuffix))
            {
                return new TemplateForExportModel(this.Name, this.Extension, this.Template)
                {
                    UsingEncoding = this.UsingEncoding,
                    EscapeCsvValues = this.EscapeCsvValues
                };
            }
            else
            {
                return new TemplateForExportModel(this.Name + nameSuffix, this.Extension, this.Template)
                {
                    UsingEncoding = this.UsingEncoding,
                    EscapeCsvValues = this.EscapeCsvValues
                };
            }
        }

        public Encoding GetEncoding()
        {
            return this.UsingEncoding switch
            {
                EncodingEnum.ANSI => Encoding.GetEncoding(0),
                EncodingEnum.UNICODE => new UnicodeEncoding(),
                EncodingEnum.UTF_8_BOM => new UTF8Encoding(true),
                _ => new UTF8Encoding(),
            };
        }

        public static GenericItemModel[] AvailableEncodings { get; } =
            new GenericItemModel[]
            {
                new GenericItemModel("UTF-8", EncodingEnum.UTF_8),
                new GenericItemModel("带 BOM 的 UTF-8", EncodingEnum.UTF_8_BOM),
                new GenericItemModel("ANSI", EncodingEnum.ANSI),
                new GenericItemModel("UNICODE", EncodingEnum.UNICODE),
            };
    }
}
