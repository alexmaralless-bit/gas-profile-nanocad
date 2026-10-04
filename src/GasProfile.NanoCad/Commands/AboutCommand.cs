using HostMgd.ApplicationServices;
using Teigha.Runtime;

namespace GasProfile.NanoCad.Commands
{
    public sealed class AboutCommand
    {
        [CommandMethod("GASPROF_ABOUT")]
        public void Run()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            document?.Editor.WriteMessage(
                "\nGasProfile.NanoCad 0.1 loaded. Main commands: GASPROF_DRAW_DRAFT, GPIPE, GREC, GSET, GVAL, GCAS, GCEDIT. Extra: GASPROF_ADD_CASING, GASPROF_EDIT_CASING, GASPROF_SETTINGS, GASPROF_CHECK, GASPROF_CHECK_TIN, GASPROF_DRAW_TIN, GASPROF_ADD_SITUATION, GASPROF_RECALC_CASINGS, GASPROF_DUMP_BLOCKS, GASPROF_DUMP_BLOCK.");
        }
    }
}
