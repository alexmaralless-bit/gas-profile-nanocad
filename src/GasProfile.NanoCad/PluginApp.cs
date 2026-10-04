using HostMgd.ApplicationServices;
using Teigha.Runtime;

namespace GasProfile.NanoCad
{
    public sealed class PluginApp : IExtensionApplication
    {
        public void Initialize()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            document?.Editor.WriteMessage(
                "\nGasProfile.NanoCad loaded. Try GASPROF_DRAW_DRAFT, GPIPE, GREC, GSET, GVAL, GCAS, GCEDIT, GASPROF_RECALC_CASINGS, GASPROF_ADD_SITUATION or GASPROF_ABOUT.");
        }

        public void Terminate()
        {
        }
    }
}
