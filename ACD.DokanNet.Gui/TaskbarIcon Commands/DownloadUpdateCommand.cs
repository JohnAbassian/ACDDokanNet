﻿namespace Azi.Cloud.DokanNet.Gui
{
    using System;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Linq;
    using System.Windows;
    using System.Windows.Input;
    using Tools;

    public class DownloadUpdateCommand : ModelBasedCommand
    {
        public override void Execute(object parameter)
        {
            try
            {
                Process.Start(Model.UpdateAvailable.Assets.First(a => a.Name == "ACDDokanNetInstaller.msi").BrowserUrl);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        public override bool CanExecute(object parameter) => Model.UpdateAvailable != null;
    }
}
