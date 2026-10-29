using System;
using System.Windows;

namespace WorkOrderDesk
{
    public partial class ActorPicker : Window
    {
        public ActorPicker()
        {
            InitializeComponent();
            try
            {
                Employees.ItemsSource = Db.Query(
                    @"SELECT DISTINCT e.BusinessEntityID, p.LastName + ', ' + p.FirstName AS Name
                      FROM HumanResources.Employee e
                      JOIN Person.Person p ON p.BusinessEntityID = e.BusinessEntityID
                      JOIN HumanResources.EmployeeDepartmentHistory h ON h.BusinessEntityID = e.BusinessEntityID AND h.EndDate IS NULL
                      JOIN HumanResources.Department d ON d.DepartmentID = h.DepartmentID
                      WHERE d.Name = 'Production' ORDER BY Name").DefaultView;
                Employees.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Cannot reach the database:\n" + ex.Message);
                Application.Current.Shutdown();
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (Employees.SelectedValue == null) return;
            App.ActorBusinessEntityID = (int)Employees.SelectedValue;
            App.ActorName = ((System.Data.DataRowView)Employees.SelectedItem)["Name"].ToString();
            DialogResult = true;
        }
    }
}
