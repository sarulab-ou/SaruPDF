using SaruPdf.Documents;
using SaruPdf.Updates;

namespace SaruPdf.Instances;

internal sealed record OpenRequest(Guid Id, string[] Paths, string[] Errors, UpdateSession? Session = null)
{
    public static OpenRequest FromArguments(string[] args, string workingDirectory)
    {
        List<string> paths = [];
        List<string> errors = [];
        foreach (var arg in args)
        {
            try { paths.Add(DocumentPath.Normalize(arg, workingDirectory)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            { errors.Add($"{arg}\n{DocumentPath.Explain(ex)}"); }
        }
        return new OpenRequest(Guid.NewGuid(), paths.ToArray(), errors.ToArray());
    }
}
