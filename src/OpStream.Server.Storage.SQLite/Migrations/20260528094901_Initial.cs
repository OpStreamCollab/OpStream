using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpStream.Server.Storage.SQLite.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentOps",
                columns: table => new
                {
                    DocumentId = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    AuthorId = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Payload = table.Column<byte[]>(type: "BLOB", nullable: false),
                    EngineType = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentOps", x => new { x.DocumentId, x.Revision });
                });

            migrationBuilder.CreateTable(
                name: "DocumentSnapshots",
                columns: table => new
                {
                    DocumentId = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    State = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentSnapshots", x => x.DocumentId);
                });

            migrationBuilder.CreateTable(
                name: "HistoryOps",
                columns: table => new
                {
                    DocumentId = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    AuthorId = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Payload = table.Column<byte[]>(type: "BLOB", nullable: false),
                    EngineType = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoryOps", x => new { x.DocumentId, x.Revision });
                });

            migrationBuilder.CreateTable(
                name: "HistorySnapshots",
                columns: table => new
                {
                    DocumentId = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    State = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistorySnapshots", x => new { x.DocumentId, x.Revision });
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentOps_DocumentId_Revision",
                table: "DocumentOps",
                columns: new[] { "DocumentId", "Revision" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoryOps_DocumentId_Revision",
                table: "HistoryOps",
                columns: new[] { "DocumentId", "Revision" });

            migrationBuilder.CreateIndex(
                name: "IX_HistorySnapshots_DocumentId_Revision",
                table: "HistorySnapshots",
                columns: new[] { "DocumentId", "Revision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentOps");

            migrationBuilder.DropTable(
                name: "DocumentSnapshots");

            migrationBuilder.DropTable(
                name: "HistoryOps");

            migrationBuilder.DropTable(
                name: "HistorySnapshots");
        }
    }
}
