using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Strim.Api.Data.Migrations
{
    public partial class InitialPlaylists : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "playlists",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_playlists", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    playlist_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    group_title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    tvg_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    tvg_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    tvg_logo = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    raw_extinf = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channels", x => x.id);
                    table.ForeignKey(
                        name: "fk_channels_playlists_playlist_id",
                        column: x => x.playlist_id,
                        principalTable: "playlists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_channels_group_title",
                table: "channels",
                column: "group_title");

            migrationBuilder.CreateIndex(
                name: "ix_channels_playlist_id_name",
                table: "channels",
                columns: new[] { "playlist_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_channels_playlist_id_sort_order",
                table: "channels",
                columns: new[] { "playlist_id", "sort_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_channels_playlist_id_url",
                table: "channels",
                columns: new[] { "playlist_id", "url" });

            migrationBuilder.CreateIndex(
                name: "ix_playlists_created_at",
                table: "playlists",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_playlists_source",
                table: "playlists",
                column: "source");

            migrationBuilder.CreateIndex(
                name: "ix_playlists_name",
                table: "playlists",
                column: "name");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channels");

            migrationBuilder.DropTable(
                name: "playlists");
        }
    }
}
