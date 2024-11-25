using InfoPanel.Drawing;
using InfoPanel.Models;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Input;
using unvell.D2DLib;
using unvell.D2DLib.WinForm;

namespace InfoPanel
{
    public class DirectDisplayWindow : D2DForm
    {
        private Point _mouseDownPos;
        public Profile Profile;

        public DirectDisplayWindow(Profile profile)
        {
            this.Profile = profile;

            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            this.BackColor = Color.Transparent;

            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            AllowTransparency = true;
            BackColor = Color.Transparent;
            //TransparencyKey = Color.Transparent;
            ShowInTaskbar = false;

            Width = Profile.Width;
            Height = Profile.Height;

            StartPosition = FormStartPosition.Manual;

            Top = Profile.WindowY;
            Left = Profile.WindowX;

            this.SetStyle(ControlStyles.ResizeRedraw, true);

            InitializeComponent();

            AnimationDraw = true;
            ShowFPS = true;
        }

        private void InitializeComponent()
        {
            this.MouseDown += new System.Windows.Forms.MouseEventHandler(MainForm_MouseDown);
            this.MouseMove += new System.Windows.Forms.MouseEventHandler(MainForm_MouseMove);
            this.MouseUp += new System.Windows.Forms.MouseEventHandler(MainForm_MouseUp);
        }

        protected override void OnRender(D2DGraphics d2dGraphics)
        {
            base.OnRender(d2dGraphics);

            using var g = new AcceleratedGraphics(d2dGraphics, Handle, 1.33f, 5, -10);
            PanelDraw.Run(Profile, g);
        }

        // Mouse down event to capture the position
        private void MainForm_MouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            // When the mouse button is pressed, capture the position relative to the form's top-left corner
            if (e.Button == MouseButtons.Left)
            {
                _mouseDownPos = e.Location;
            }

            if (e.Button == MouseButtons.Middle)
            {
                var selectedProfile = SharedModel.Instance.SelectedProfile;

                if (selectedProfile != Profile)
                {
                    return;
                }

                DisplayItem? clickedItem = null;
                Rectangle clickedItemBounds = new Rectangle(0, 0, 0, 0);

                var displayItems = SharedModel.Instance.DisplayItems?.ToList();

                if (displayItems != null)
                {
                    foreach (var item in displayItems)
                    {
                        if (item.Hidden)
                        {
                            continue;
                        }

                        var itemBounds = item.EvaluateBounds();

                        if (itemBounds.Width >= Profile.Width && itemBounds.Height >= Profile.Height && itemBounds.X == 0 && itemBounds.Y == 0)
                        {
                            continue;
                        }

                        if (itemBounds.Contains(e.X, e.Y))
                        {
                            clickedItem = item;
                            clickedItemBounds = new Rectangle((int)itemBounds.X, (int)itemBounds.Y, (int)itemBounds.Width, (int)itemBounds.Height);
                        }
                    }

                    if (clickedItem != null)
                    {
                        if (SharedModel.Instance.SelectedItem == null || !Keyboard.IsKeyDown(Key.LeftCtrl))
                        {
                            SharedModel.Instance.SelectedItem = clickedItem;

                            if (!Keyboard.IsKeyDown(Key.LeftCtrl))
                            {
                                SharedModel.Instance.SelectedItems?.ForEach(item =>
                                {
                                    if (item != clickedItem)
                                    {
                                        item.Selected = false;
                                    }
                                });
                            }
                        }

                        clickedItem.Selected = true;
                    }
                    else
                    {
                        SharedModel.Instance.SelectedItem = null;
                        SharedModel.Instance.SelectedItems?.ForEach(item =>
                        {
                            item.Selected = false;
                        });
                    }
                }
            }
        }

        // Mouse move event to move the form
        private void MainForm_MouseMove(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            // If the mouse button is pressed, move the form
            if (e.Button == MouseButtons.Left)
            {
                // Calculate how far the mouse has moved
                var diffX = e.X - _mouseDownPos.X;
                var diffY = e.Y - _mouseDownPos.Y;

                // Move the form to the new position
                this.Location = new Point(this.Left + diffX, this.Top + diffY);
            }
        }

        // Optional: Mouse up event to stop dragging
        private void MainForm_MouseUp(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            // Optional: Handle mouse up if needed (for example, you could reset flags)
        }
    }
}
