using System.Collections.Generic;
using Teigha.DatabaseServices;
using Teigha.Runtime;

namespace GasProfile.NanoCad.Commands
{
    internal static class ProfileEntityXData
    {
        internal const string AppName = "GASPROF_ENTITY";
        internal const string Version = "GASPROF_ENTITY_V1";
        internal const string RoleGround = "GROUND";
        internal const string RolePipe = "PIPE";
        internal const string RoleGrid = "GRID";
        internal const string RoleTable = "TABLE";
        internal const string RoleUnfoldedPlan = "UNFOLDED_PLAN";
        internal const string RoleProfileSituation = "PROFILE_SITUATION";
        internal const string RoleProfileLabel = "PROFILE_LABEL";

        internal static void EnsureRegApp(Database database, Transaction transaction)
        {
            var table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
            if (table.Has(AppName))
            {
                return;
            }

            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = AppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        internal static void Attach(Entity entity, string sessionId, string role)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(role))
            {
                return;
            }

            entity.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, Version),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, sessionId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, role));
        }

        internal static bool TryRead(Entity entity, out ProfileEntityMetadata metadata)
        {
            metadata = default!;
            var buffer = entity.GetXDataForApplication(AppName);
            if (buffer == null)
            {
                return false;
            }

            var values = new List<TypedValue>(buffer.AsArray());
            if (values.Count < 4 ||
                values[1].Value as string != Version ||
                !(values[2].Value is string sessionId) ||
                !(values[3].Value is string role) ||
                string.IsNullOrWhiteSpace(sessionId) ||
                string.IsNullOrWhiteSpace(role))
            {
                return false;
            }

            metadata = new ProfileEntityMetadata(sessionId, role);
            return true;
        }
    }

    internal sealed class ProfileEntityMetadata
    {
        internal ProfileEntityMetadata(string sessionId, string role)
        {
            SessionId = sessionId;
            Role = role;
        }

        internal string SessionId { get; }

        internal string Role { get; }
    }
}
