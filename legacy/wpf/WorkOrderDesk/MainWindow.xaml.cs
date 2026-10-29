using System;
using System.Data;
using System.Data.SqlClient;
using System.Windows;
using System.Windows.Controls;

namespace WorkOrderDesk
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                if (new ActorPicker().ShowDialog() != true) { Close(); return; }
                ActorText.Text = "Signed in as " + App.ActorName;
                NewDue.Text = DateTime.Today.AddDays(14).ToString("yyyy-MM-dd");
                LoadOrders();
            };
        }

        private void LoadOrders()
        {
            var filter = ((ComboBoxItem)StatusFilter.SelectedItem).Content.ToString();
            try
            {
                Grid.ItemsSource = Db.Query(
                    @"SELECT TOP (500) WorkOrderID, ProductID, ProductName, OrderQty, ScrappedQty, StartDate, DueDate, EndDate, Status
                      FROM dbo.vWorkOrderStatus
                      WHERE @s = 'All' OR Status = @s
                      ORDER BY WorkOrderID DESC",
                    new SqlParameter("@s", filter)).DefaultView;
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private int? SelectedId()
        {
            var row = Grid.SelectedItem as DataRowView;
            return row == null ? (int?)null : (int)row["WorkOrderID"];
        }

        private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded && Grid != null) LoadOrders(); }
        private void Refresh_Click(object sender, RoutedEventArgs e) { LoadOrders(); }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var id = new SqlParameter("@WorkOrderID", SqlDbType.Int) { Direction = ParameterDirection.Output };
                Db.Proc("dbo.uspWorkOrderCreate",
                    new SqlParameter("@ProductID", int.Parse(NewProduct.Text)),
                    new SqlParameter("@OrderQty", int.Parse(NewQty.Text)),
                    new SqlParameter("@DueDate", DateTime.Parse(NewDue.Text)),
                    new SqlParameter("@ActorBusinessEntityID", App.ActorBusinessEntityID), id);
                LoadOrders();
                MessageBox.Show("Created work order " + id.Value);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void Release_Click(object sender, RoutedEventArgs e)
        {
            var id = SelectedId();
            if (id == null) return;
            try
            {
                Db.Proc("dbo.uspWorkOrderRelease",
                    new SqlParameter("@WorkOrderID", id.Value),
                    new SqlParameter("@ActorBusinessEntityID", App.ActorBusinessEntityID));
                LoadOrders();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var id = SelectedId();
            if (id == null) return;
            new DetailWindow(id.Value) { Owner = this }.ShowDialog();
            LoadOrders();
        }
    }
}
