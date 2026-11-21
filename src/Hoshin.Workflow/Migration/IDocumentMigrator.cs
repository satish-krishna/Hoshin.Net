namespace Hoshin.Workflow.Migration;

/// <summary>Result for one file evaluated or migrated by <see cref="IDocumentMigrator"/>.</summary>
public sealed record DocumentMigrationItem(string Path, string? To, string Kind, string Status, string? Detail);

/// <summary>Aggregate result of migrating one or more files or directories to Layout 2 JSONC and <c>change.jsonc</c>.</summary>
public sealed record DocumentMigrationResult(bool Write, List<DocumentMigrationItem> Files, List<string> Problems);

/// <summary>
/// Migrates legacy YAML Markdown documents (Layout 1 and Layout 2) and <c>change.yml</c> files into canonical
/// Layout 2 JSONC and <c>change.jsonc</c>. Idempotent and dry-run capable.
/// </summary>
public interface IDocumentMigrator
{
    string MigrateMarkdownText(string text, string filePath, out bool changed);

    string MigrateChangeYamlText(string yamlText);

    DocumentMigrationResult MigratePaths(IReadOnlyList<string> paths, bool write);

    List<DocumentMigrationItem> PlanDirectory(string directory);
}
