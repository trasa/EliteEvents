using EliteEvents.Web.Eddn;

namespace EliteEvents.Web.Services.Eddn;

public class EddnMessageParser
{
    private static readonly Dictionary<string, Type> SchemaToType = new()
    {
        ["https://eddn.edcd.io/schemas/journal/1"] = typeof(JournalMessage),
    };
}
