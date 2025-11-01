using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#if APRSGATEWAY
[assembly: AssemblyTitle("APRSGateway")]
[assembly: AssemblyProduct("APRSGateway")]
[assembly: Guid("516d1007-466a-4a75-b566-c376f6a813ab")]
#else
[assembly: AssemblyTitle("APRSServer")]
[assembly: AssemblyProduct("APRSServer")]
[assembly: Guid("c298c671-b64f-4204-a7d1-edccdb7c02ea")]
#endif
#if APRSGATEWAY
[assembly: AssemblyDescription("APRSGateway")]
#else
[assembly: AssemblyDescription("APRSServer")]
#endif
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("openAPRS")]
[assembly: AssemblyCopyright("13MAD86 / Martin D.")]
[assembly: AssemblyTrademark("openAPRS")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
