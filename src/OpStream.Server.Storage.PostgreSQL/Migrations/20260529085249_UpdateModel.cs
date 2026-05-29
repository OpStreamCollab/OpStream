using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpStream.Server.Storage.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class UpdateModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Comments",
                schema: "opstream",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    DocumentId = table.Column<string>(type: "text", nullable: false),
                    ParentCommentId = table.Column<string>(type: "text", nullable: true),
                    AuthorPeerId = table.Column<string>(type: "text", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    AnchorJson = table.Column<string>(type: "text", nullable: true),
                    AnchoredAtRevision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedByPeerId = table.Column<string>(type: "text", nullable: true),
                    IsOrphaned = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DocumentBranches",
                schema: "opstream",
                columns: table => new
                {
                    GlobalName = table.Column<string>(type: "text", nullable: false),
                    BranchId = table.Column<string>(type: "text", nullable: false),
                    PhysicalDocumentId = table.Column<string>(type: "text", nullable: false),
                    ForkParentBranchId = table.Column<string>(type: "text", nullable: true),
                    ForkRevision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsReadOnly = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentBranches", x => new { x.GlobalName, x.BranchId });
                });

            migrationBuilder.CreateTable(
                name: "DocumentNames",
                schema: "opstream",
                columns: table => new
                {
                    GlobalName = table.Column<string>(type: "text", nullable: false),
                    DefaultBranchId = table.Column<string>(type: "text", nullable: false),
                    EngineType = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentNames", x => x.GlobalName);
                });

            migrationBuilder.CreateTable(
                name: "DocumentVersions",
                schema: "opstream",
                columns: table => new
                {
                    GlobalName = table.Column<string>(type: "text", nullable: false),
                    BranchId = table.Column<string>(type: "text", nullable: false),
                    Tag = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    HistorySnapshotName = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentVersions", x => new { x.GlobalName, x.BranchId, x.Tag });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_DocumentId_ParentCommentId_ResolvedAt",
                schema: "opstream",
                table: "Comments",
                columns: new[] { "DocumentId", "ParentCommentId", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentBranches_PhysicalDocumentId",
                schema: "opstream",
                table: "DocumentBranches",
                column: "PhysicalDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentVersions_GlobalName_BranchId",
                schema: "opstream",
                table: "DocumentVersions",
                columns: new[] { "GlobalName", "BranchId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Comments",
                schema: "opstream");

            migrationBuilder.DropTable(
                name: "DocumentBranches",
                schema: "opstream");

            migrationBuilder.DropTable(
                name: "DocumentNames",
                schema: "opstream");

            migrationBuilder.DropTable(
                name: "DocumentVersions",
                schema: "opstream");
        }
    }
}
