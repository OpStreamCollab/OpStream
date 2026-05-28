using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpStream.Server.Storage.MySQL.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "opstream");

            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DocumentOps",
                schema: "opstream",
                columns: table => new
                {
                    DocumentId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    AuthorId = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    Payload = table.Column<byte[]>(type: "longblob", nullable: false),
                    EngineType = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentOps", x => new { x.DocumentId, x.Revision });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DocumentSnapshots",
                schema: "opstream",
                columns: table => new
                {
                    DocumentId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    State = table.Column<byte[]>(type: "longblob", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentSnapshots", x => x.DocumentId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "HistoryOps",
                schema: "opstream",
                columns: table => new
                {
                    DocumentId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    AuthorId = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    Payload = table.Column<byte[]>(type: "longblob", nullable: false),
                    EngineType = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoryOps", x => new { x.DocumentId, x.Revision });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "HistorySnapshots",
                schema: "opstream",
                columns: table => new
                {
                    DocumentId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    State = table.Column<byte[]>(type: "longblob", nullable: false),
                    Name = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistorySnapshots", x => new { x.DocumentId, x.Revision });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentOps_DocumentId_Revision",
                schema: "opstream",
                table: "DocumentOps",
                columns: new[] { "DocumentId", "Revision" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoryOps_DocumentId_Revision",
                schema: "opstream",
                table: "HistoryOps",
                columns: new[] { "DocumentId", "Revision" });

            migrationBuilder.CreateIndex(
                name: "IX_HistorySnapshots_DocumentId_Revision",
                schema: "opstream",
                table: "HistorySnapshots",
                columns: new[] { "DocumentId", "Revision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentOps",
                schema: "opstream");

            migrationBuilder.DropTable(
                name: "DocumentSnapshots",
                schema: "opstream");

            migrationBuilder.DropTable(
                name: "HistoryOps",
                schema: "opstream");

            migrationBuilder.DropTable(
                name: "HistorySnapshots",
                schema: "opstream");
        }
    }
}
