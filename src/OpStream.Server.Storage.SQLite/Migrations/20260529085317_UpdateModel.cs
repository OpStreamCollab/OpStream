using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpStream.Server.Storage.SQLite.Migrations
{
    /// <inheritdoc />
    public partial class UpdateModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Comments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    DocumentId = table.Column<string>(type: "TEXT", nullable: false),
                    ParentCommentId = table.Column<string>(type: "TEXT", nullable: true),
                    AuthorPeerId = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    AnchorJson = table.Column<string>(type: "TEXT", nullable: true),
                    AnchoredAtRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ResolvedByPeerId = table.Column<string>(type: "TEXT", nullable: true),
                    IsOrphaned = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DocumentBranches",
                columns: table => new
                {
                    GlobalName = table.Column<string>(type: "TEXT", nullable: false),
                    BranchId = table.Column<string>(type: "TEXT", nullable: false),
                    PhysicalDocumentId = table.Column<string>(type: "TEXT", nullable: false),
                    ForkParentBranchId = table.Column<string>(type: "TEXT", nullable: true),
                    ForkRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsReadOnly = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentBranches", x => new { x.GlobalName, x.BranchId });
                });

            migrationBuilder.CreateTable(
                name: "DocumentNames",
                columns: table => new
                {
                    GlobalName = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultBranchId = table.Column<string>(type: "TEXT", nullable: false),
                    EngineType = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentNames", x => x.GlobalName);
                });

            migrationBuilder.CreateTable(
                name: "DocumentVersions",
                columns: table => new
                {
                    GlobalName = table.Column<string>(type: "TEXT", nullable: false),
                    BranchId = table.Column<string>(type: "TEXT", nullable: false),
                    Tag = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    HistorySnapshotName = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentVersions", x => new { x.GlobalName, x.BranchId, x.Tag });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_DocumentId_ParentCommentId_ResolvedAt",
                table: "Comments",
                columns: new[] { "DocumentId", "ParentCommentId", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentBranches_PhysicalDocumentId",
                table: "DocumentBranches",
                column: "PhysicalDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentVersions_GlobalName_BranchId",
                table: "DocumentVersions",
                columns: new[] { "GlobalName", "BranchId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Comments");

            migrationBuilder.DropTable(
                name: "DocumentBranches");

            migrationBuilder.DropTable(
                name: "DocumentNames");

            migrationBuilder.DropTable(
                name: "DocumentVersions");
        }
    }
}
