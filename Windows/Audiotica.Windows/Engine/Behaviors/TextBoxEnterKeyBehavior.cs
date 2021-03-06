﻿using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Markup;
using Microsoft.Xaml.Interactivity;

namespace Audiotica.Windows.Engine.Behaviors
{
    // DOCS: https://github.com/Windows-XAML/Template10/wiki/Docs-%7C-XamlBehaviors
    [ContentProperty(Name = nameof(Actions))]
    [TypeConstraint(typeof(TextBox))]
    public class TextBoxEnterKeyBehavior : DependencyObject, IBehavior
    {
        private TextBox AssociatedTextBox => AssociatedObject as TextBox;
        public DependencyObject AssociatedObject { get; private set; }

        public void Attach(DependencyObject associatedObject)
        {
            AssociatedObject = associatedObject;
            AssociatedTextBox.KeyUp += AssociatedTextBox_KeyUp;
        }

        public void Detach()
        {
            AssociatedTextBox.KeyUp -= AssociatedTextBox_KeyUp;
        }

        private void AssociatedTextBox_KeyUp(object sender, global::Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == global::Windows.System.VirtualKey.Enter)
            {
                Interaction.ExecuteActions(AssociatedObject, this.Actions, null);
                e.Handled = true;
            }
        }

        public ActionCollection Actions
        {
            get
            {
                var actions = (ActionCollection)base.GetValue(ActionsProperty);
                if (actions == null)
                {
                    base.SetValue(ActionsProperty, actions = new ActionCollection());
                }
                return actions;
            }
        }
        public static readonly DependencyProperty ActionsProperty = 
            DependencyProperty.Register("Actions", typeof(ActionCollection), 
                typeof(TextBoxEnterKeyBehavior), new PropertyMetadata(null));
    }
}
