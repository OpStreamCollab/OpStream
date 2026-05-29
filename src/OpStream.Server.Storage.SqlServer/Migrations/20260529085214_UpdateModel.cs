using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpStream.Server.Storage.SqlServer.Migrations
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
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    DocumentId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ParentCommentId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    AuthorPeerId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AnchorJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AnchoredAtRevision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedByPeerId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsOrphaned = table.Column<bool>(type: "bit", nullable: false)
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
                    GlobalName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    BranchId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    PhysicalDocumentId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ForkParentBranchId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ForkRevision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsReadOnly = table.Column<bool>(type: "bit", nullable: false)
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
                    GlobalName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    DefaultBranchId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EngineType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
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
                    GlobalName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    BranchId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Tag = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    HistorySnapshotName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
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
