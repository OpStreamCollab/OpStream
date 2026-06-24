using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpStream.Server.Storage.MySQL.Migrations
{
    /// <inheritdoc />
    public partial class AddCommentAuthorName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthorName",
                schema: "opstream",
                table: "Comments",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthorName",
                schema: "opstream",
                table: "Comments");
        }
    }
}
