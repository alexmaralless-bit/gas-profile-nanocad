using System.Reflection;
using System.Runtime.InteropServices;
using GasProfile.NanoCad;
using GasProfile.NanoCad.Commands;
using Teigha.Runtime;

[assembly: ExtensionApplication(typeof(PluginApp))]
[assembly: CommandClass(typeof(AboutCommand))]
[assembly: CommandClass(typeof(AddCasingCommand))]
[assembly: CommandClass(typeof(AddSituationCommand))]
[assembly: CommandClass(typeof(CheckCommand))]
[assembly: CommandClass(typeof(CheckPipeCommand))]
[assembly: CommandClass(typeof(CheckTinCommand))]
[assembly: CommandClass(typeof(CommandAliases))]
[assembly: CommandClass(typeof(DrawDraftCommand))]
[assembly: CommandClass(typeof(DrawTinCommand))]
[assembly: CommandClass(typeof(DumpBlocksCommand))]
[assembly: CommandClass(typeof(EditCasingCommand))]
[assembly: CommandClass(typeof(RecalcCasingsCommand))]
[assembly: CommandClass(typeof(RecalcProfileCommand))]
[assembly: CommandClass(typeof(SettingsCommand))]
[assembly: CommandClass(typeof(ValidateCommand))]

[assembly: AssemblyTitle("GasProfile.NanoCad")]
[assembly: AssemblyDescription("Longitudinal gas pipeline profile tools for nanoCAD.")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("GasProfile.NanoCad")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyTrademark("")]
[assembly: ComVisible(false)]
[assembly: Guid("2B868D53-5913-4D83-888F-C183CB30F7BA")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
