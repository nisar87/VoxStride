using System.Windows.Forms;

namespace VoxStride.UiFixture;

/// <summary>Deterministic window for UI Automation tests: text box, Greet button → label, checkbox, a "dangerous" button.</summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var title = args.Length > 0 ? args[0] : "VoxStride UI Fixture";
        var form = new Form { Text = title, Width = 520, Height = 300, StartPosition = FormStartPosition.Manual, Left = 120, Top = 120 };
        var name = new TextBox { Left = 20, Top = 20, Width = 300, AccessibleName = "Your name", Name = "NameBox" };
        var greet = new Button { Left = 340, Top = 18, Width = 120, Height = 32, Text = "Greet", Name = "GreetButton" };
        var greeting = new Label { Left = 20, Top = 70, Width = 440, Height = 24, Text = "Nobody greeted yet", Name = "GreetingLabel" }; // no AccessibleName: UIA Name must follow the text
        var subscribe = new CheckBox { Left = 20, Top = 110, Width = 200, Text = "Subscribe", Name = "SubscribeBox" };
        var danger = new Button { Left = 20, Top = 150, Width = 200, Height = 32, Text = "Delete everything", Name = "DangerButton" };
        var notes = new TextBox { Left = 20, Top = 195, Width = 440, AccessibleName = "Notes", Name = "NotesBox" };
        greet.Click += (_, _) => greeting.Text = "Hello, " + name.Text + "!";
        danger.Click += (_, _) => greeting.Text = "DANGER PRESSED";
        form.Controls.AddRange([name, greet, greeting, subscribe, danger, notes]);
        Application.Run(form);
    }
}
