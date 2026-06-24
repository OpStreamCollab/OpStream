using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpStream.Server.Storage.SqlServer.Migrations
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
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
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
