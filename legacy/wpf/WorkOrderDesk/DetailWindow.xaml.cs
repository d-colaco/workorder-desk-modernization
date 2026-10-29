using System;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Windows;

namespace WorkOrderDesk
{
    public partial class DetailWindow : Window
    {
        private readonly int _id;

        public DetailWindow(int workOrderId)
        {
            InitializeComponent();
            _id = workOrderId;
            Reload();
        }

        private void Reload()
        {
            try
            {
                var h = Db.Query("SELECT * FROM dbo.vWorkOrderStatus WHERE WorkOrderID = @id", new SqlParameter("@id", _id));
                if (h.Rows.Count == 0) { Header.Text = "Work order " + _id + " not found"; return; }
                var r = h.Rows[0];
                Header.Text = string.Format("Work order {0}: {1} x {2} ({3}), scrapped {4}", _id, r["OrderQty"], r["ProductName"], r["Status"], r["ScrappedQty"]);
                Routing.ItemsSource = Db.Query(
                    @"SELECT r.OperationSequence, l.Name AS WorkCenter, r.ScheduledStartDate, r.ScheduledEndDate,
                             r.ActualStartDate, r.ActualEndDate, r.ActualResourceHrs, r.PlannedCost, r.ActualCost
                      FROM Production.WorkOrderRouting r
                      JOIN Production.Location l ON l.LocationID = r.LocationID
                      WHERE r.WorkOrderID = @id ORDER BY r.OperationSequence", new SqlParameter("@id", _id)).DefaultView;
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void Routing_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var row = Routing.SelectedItem as DataRowView;
            if (row == null) return;
            ActStart.Text = Fmt(row["ActualStartDate"], "yyyy-MM-dd HH:mm");
            ActEnd.Text = Fmt(row["ActualEndDate"], "yyyy-MM-dd HH:mm");
            ActHrs.Text = row["ActualResourceHrs"] is DBNull ? "" : Convert.ToString(row["ActualResourceHrs"], CultureInfo.InvariantCulture);
            ActCost.Text = row["ActualCost"] is DBNull ? "" : Convert.ToString(row["ActualCost"], CultureInfo.InvariantCulture);
        }

        private static string Fmt(object v, string f) { return v is DBNull ? "" : ((DateTime)v).ToString(f); }
        private static object OrNull(string s, Func<string, object> parse) { return string.IsNullOrWhiteSpace(s) ? DBNull.Value : parse(s.Trim()); }

        private void Record_Click(object sender, RoutedEventArgs e)
        {
            var row = Routing.SelectedItem as DataRowView;
            if (row == null) return;
            try
            {
                Db.Proc("dbo.uspWorkOrderRoutingRecord",
                    new SqlParameter("@WorkOrderID", _id),
                    new SqlParameter("@OperationSequence", Convert.ToInt16(row["OperationSequence"])),
                    new SqlParameter("@ActualStartDate", DateTime.Parse(ActStart.Text)),
                    new SqlParameter("@ActualEndDate", OrNull(ActEnd.Text, s => DateTime.Parse(s))),
                    new SqlParameter("@ActualResourceHrs", OrNull(ActHrs.Text, s => decimal.Parse(s, CultureInfo.InvariantCulture))),
                    new SqlParameter("@ActualCost", OrNull(ActCost.Text, s => decimal.Parse(s, CultureInfo.InvariantCulture))),
                    new SqlParameter("@ActorBusinessEntityID", App.ActorBusinessEntityID));
                Reload();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void Scrap_Click(object sender, RoutedEventArgs e)
        {
            if (new ScrapDialog(_id) { Owner = this }.ShowDialog() == true) Reload();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Db.Proc("dbo.uspWorkOrderClose",
                    new SqlParameter("@WorkOrderID", _id),
                    new SqlParameter("@ActorBusinessEntityID", App.ActorBusinessEntityID));
                Reload();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }
    }
}
