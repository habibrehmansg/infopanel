using InfoPanel.DataApi;
using InfoPanel.Plugins;
using System.Data;
using System.Text;
using System.Text.Json;
using Xunit;

namespace InfoPanel.Tests
{
    public class DataApiTableTests
    {
        private static DataTable Table()
        {
            var table = new DataTable();
            table.Columns.Add("Process", typeof(PluginText));
            table.Columns.Add("CPU", typeof(PluginSensor));
            table.Columns.Add("Note", typeof(string));
            table.Rows.Add(new PluginText("p", "a, b"), new PluginSensor("c", "CPU", 12.34f, "%"), "plain");
            table.Rows.Add(new PluginText("p", "idle"), new PluginSensor("c", "CPU", 100f, "%"), "plain");
            return table;
        }

        [Fact]
        public void Keeps_column_names_and_each_cell_as_InfoPanel_draws_it()
        {
            var result = SensorSnapshotBuilder.ToTable(Table())!;

            Assert.Equal(["Process", "CPU", "Note"], result.Columns);
            // A cell holding ", " stays one cell; a cell that is not plugin data is not drawn.
            Assert.Equal(["a, b", "12.3%", null], result.Rows[0]);
            Assert.Equal(["idle", "100%", null], result.Rows[1]);
        }

        [Fact]
        public void Tables_compare_by_content()
        {
            Assert.Equal(SensorSnapshotBuilder.ToTable(Table()), SensorSnapshotBuilder.ToTable(Table()));
            var a = new DataApiValue { Text = "x", Table = SensorSnapshotBuilder.ToTable(Table()) };
            var b = new DataApiValue { Text = "x", Table = SensorSnapshotBuilder.ToTable(Table()) };
            Assert.Equal(a, b);

            var changed = Table();
            changed.Rows[1][0] = new PluginText("p", "busy");
            Assert.NotEqual(SensorSnapshotBuilder.ToTable(Table()), SensorSnapshotBuilder.ToTable(changed));
        }

        [Fact]
        public void Serializes_as_columns_and_rows()
        {
            var json = JsonSerializer.Serialize(new DataApiValue { Table = SensorSnapshotBuilder.ToTable(Table()) }, DataApiTask.JsonOptions);

            Assert.Contains("\"table\":{\"columns\":[\"Process\",\"CPU\",\"Note\"],\"rows\":[[\"a, b\",\"12.3%\",null]", json);
        }

        [Fact]
        public void No_table_is_null()
        {
            Assert.Null(SensorSnapshotBuilder.ToTable(null));
        }
    }

    public class DataApiFontTests
    {
        [Fact]
        public void Resolves_an_installed_font_to_its_face_file()
        {
            var resolved = DataApiFonts.Resolve(new FontRequest("Arial", "", true, false));

            Assert.Equal("Arial", resolved.Family);
            Assert.Equal(700, resolved.Weight);
            Assert.False(resolved.Fallback);
            Assert.NotNull(resolved.Face);
            Assert.True(DataApiFonts.TryGetFace(resolved.Face!, out var bytes, out var contentType));
            Assert.Equal("font/ttf", contentType);
            Assert.Equal(new byte[] { 0, 1, 0, 0 }, bytes[..4]);
        }

        [Fact]
        public void Reports_a_missing_family_as_a_fallback()
        {
            var resolved = DataApiFonts.Resolve(new FontRequest("No Such Font 4f1c", null, false, false));

            Assert.True(resolved.Fallback);
            Assert.NotEqual("No Such Font 4f1c", resolved.Family);
        }

        [Fact]
        public void Serves_only_faces_a_resolve_returned()
        {
            Assert.False(DataApiFonts.TryGetFace("0123456789abcdef", out _, out _));
            Assert.False(DataApiFonts.TryGetFace("../../windows/fonts/arial.ttf", out _, out _));
        }

        [Fact]
        public void Rejects_overlong_names()
        {
            Assert.False(DataApiFonts.IsValid(new FontRequest(new string('a', 300), null, false, false)));
            Assert.False(DataApiFonts.IsValid(new FontRequest("Arial", new string('a', 300), false, false)));
            Assert.True(DataApiFonts.IsValid(new FontRequest("Arial", "Bold", false, false)));
        }
    }
}
