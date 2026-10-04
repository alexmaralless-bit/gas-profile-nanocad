using Teigha.Runtime;

namespace GasProfile.NanoCad.Commands
{
    public sealed class CommandAliases
    {
        [CommandMethod("GPIPE")]
        public void CheckPipe()
        {
            new CheckPipeCommand().Run();
        }

        [CommandMethod("GREC")]
        public void RecalcProfile()
        {
            new RecalcProfileCommand().Run();
        }

        [CommandMethod("GVAL")]
        public void Validate()
        {
            new ValidateCommand().Run();
        }

        [CommandMethod("GSET")]
        public void Settings()
        {
            new SettingsCommand().Run();
        }

        [CommandMethod("GCAS")]
        public void AddCasing()
        {
            new AddCasingCommand().Run();
        }

        [CommandMethod("GCEDIT")]
        public void EditCasing()
        {
            new EditCasingCommand().Run();
        }
    }
}
