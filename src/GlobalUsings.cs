// Global using directives — resolve ambiguities caused by having both
// UseWPF and UseWindowsForms enabled simultaneously, and ensure System.IO
// types are available in the WPF temp project the compiler creates internally.

global using System.IO;

// Alias the most frequently ambiguous types project-wide
global using MessageBox   = System.Windows.MessageBox;
global using Color        = System.Windows.Media.Color;
global using Orientation  = System.Windows.Controls.Orientation;
