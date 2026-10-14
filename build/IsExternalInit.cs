// init 访问器需要 IsExternalInit 标记类型。
// netstandard2.0 与 .NET Framework（net472）的 BCL 均未提供，需自行补充；
// net6+ 由运行时提供，故不编译本文件。
#if NETSTANDARD2_0 || NET472
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
