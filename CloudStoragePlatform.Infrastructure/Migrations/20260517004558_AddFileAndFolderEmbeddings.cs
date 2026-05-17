using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudStoragePlatform.Infrastructure.Migrations
{
    public partial class AddFileAndFolderEmbeddings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileEmbeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    VectorId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Model = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Dimension = table.Column<int>(type: "int", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EmbeddedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileEmbeddings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileEmbeddings_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "FileId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FolderEmbeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VectorId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FileCount = table.Column<int>(type: "int", nullable: false),
                    LastComputedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsStale = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderEmbeddings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FolderEmbeddings_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "FolderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileEmbeddings_FileId",
                table: "FileEmbeddings",
                column: "FileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileEmbeddings_Status",
                table: "FileEmbeddings",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_FileEmbeddings_UserId",
                table: "FileEmbeddings",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_FolderEmbeddings_FolderId",
                table: "FolderEmbeddings",
                column: "FolderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FolderEmbeddings_UserId_IsStale",
                table: "FolderEmbeddings",
                columns: new[] { "UserId", "IsStale" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileEmbeddings");

            migrationBuilder.DropTable(
                name: "FolderEmbeddings");
        }
    }
}
