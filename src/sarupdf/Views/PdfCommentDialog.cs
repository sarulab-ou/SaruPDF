namespace SaruPdf.Views;

internal sealed class PdfCommentDialog : Form
{
    private readonly TextBox comment = new() { Name = "CommentText", Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AccessibleName = "コメント本文" };
    internal string CommentText => comment.Text;
    internal PdfCommentDialog(string quote, string? existing = null)
    {
        Text = existing is null ? "コメントを追加" : "コメント";
        ClientSize = new Size(520, 340); MinimumSize = new Size(380, 260);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        AppIcon.Apply(this);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.Controls.Add(new TextBox { Text = quote, Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, AccessibleName = "選択した本文" }, 0, 0);
        layout.Controls.Add(new Label { Text = existing is null ? "コメントを入力（Ctrl+Enterで追加）" : "保存されているコメント", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        comment.Text = (existing ?? "").ReplaceLineEndings(Environment.NewLine); comment.ReadOnly = existing is not null;
        layout.Controls.Add(comment, 0, 2);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
        var cancel = new Button { Text = existing is null ? "キャンセル" : "閉じる", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel); CancelButton = cancel;
        if (existing is null)
        {
            var add = new Button { Text = "追加", AutoSize = true, DialogResult = DialogResult.OK, Enabled = false, Name = "AddComment" };
            comment.TextChanged += (_, _) => add.Enabled = !string.IsNullOrWhiteSpace(comment.Text);
            buttons.Controls.Add(add); AcceptButton = add;
        }
        layout.Controls.Add(buttons, 0, 3); Controls.Add(layout);
        Shown += (_, _) => comment.Focus();
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Enter) && !comment.ReadOnly && !string.IsNullOrWhiteSpace(comment.Text))
        { DialogResult = DialogResult.OK; Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
