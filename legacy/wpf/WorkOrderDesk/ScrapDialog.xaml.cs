using System;
using System.Data.SqlClient;
using System.Windows;

namespace WorkOrderDesk
{
    public partial class ScrapDialog : Window
    {
        private readonly int _id;

        public ScrapDialog(int workOrderId)
        {
            InitializeComponent();
            _id = workOrderId;
            Reason.ItemsSource = Db.Query("SELECT ScrapReasonID, Name FROM Production.ScrapReason ORDER BY Name").DefaultView;
            Reason.SelectedIndex = 0;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Db.Proc("dbo.uspWorkOrderScrap",
                    new SqlParameter("@WorkOrderID", _id),
                    new SqlParameter("@ScrapQty", short.Parse(Qty.Text)),
                    new SqlParameter("@ScrapReasonID", Convert.ToInt16(Reason.SelectedValue)),
                    new SqlParameter("@ActorBusinessEntityID", App.ActorBusinessEntityID));
                DialogResult = true;
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }
    }
}
