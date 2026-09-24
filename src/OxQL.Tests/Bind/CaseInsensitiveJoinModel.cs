using OxQL.Model;
using OxQL.Model.Attributes;
using OxQL.Model.Build;

namespace OxQL.Tests.Bind;

/// <summary>
/// Two entities joined on a string key, declared by hand so they stay out of the probe graph:
/// a folder, and the documents that name their folder by its code. The collections carry the
/// prefix a live test may create and drop.
/// </summary>
internal static class CaseInsensitiveJoinModel
{
    public const string Folder = "ci.folder";
    public const string Document = "ci.document";
    public const string FolderCollection = "tmp_ci_folders";
    public const string DocumentCollection = "tmp_ci_documents";

    public static readonly EntityModel Model = ClrModelBuilder.Build(
    [
        new EntityDeclaration(Folder, Folder, typeof(FolderModel), FolderCollection, null, false),
        new EntityDeclaration(Document, Document, typeof(DocumentModel), DocumentCollection, null, false),
    ]);

    public sealed class FolderModel
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        public string Code { get; set; } = "";

        public string Name { get; set; } = "";
    }

    public sealed class DocumentModel
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        [OxQLReference(Folder, "code")]
        public string? FolderCode { get; set; }

        public string Title { get; set; } = "";
    }
}
