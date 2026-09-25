using System.IO;

namespace RenpyTranslator;

/// <summary>面向用户的诊断信息：消息为中文且可直接展示，不会被当作系统英文异常替换。</summary>
public sealed class UserError : IOException
{
    public UserError(string message, Exception? inner = null) : base(message, inner) { }
}
