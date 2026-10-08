// .NET 10 SDK 起默认隐式 using 不再含 System.IO/System.Net.Http；主项目源码是显式 using,
// 测试文件按老习惯写的，这里统一补齐（xunit.v3 也不自动注入 global using Xunit）。
global using Xunit;
global using System.IO;
