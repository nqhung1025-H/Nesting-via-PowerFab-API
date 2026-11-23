using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class MaterialPlanDialog : Form
    {
        private readonly NestingSnapshot snapshot;
        private readonly PlateNestGroup group;
        private readonly CheckedListBox stockList=new CheckedListBox{Dock=DockStyle.Fill,CheckOnClick=true,IntegralHeight=false,HorizontalScrollbar=true};
        private readonly CheckBox purchase=new CheckBox{Text="Add new sheets for any shortage",AutoSize=true,Checked=true,Anchor=AnchorStyles.Left,Margin=Padding.Empty};
        private readonly NumericUpDown length=Number(),width=Number();
        private readonly TextBox requisition=new TextBox{Dock=DockStyle.Fill};
        private readonly string suggestedRequisition;
        private readonly bool automaticRequisition;
        private readonly ComboBox destination=new ComboBox{Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList};
        private readonly TextBox po=new TextBox{Dock=DockStyle.Fill,Enabled=false};
        private readonly AbeNestDocument? abe;
        private readonly Action<MaterialPlan>? validatePlan;
        private bool changingStock;
        public MaterialPlan? Plan {get;private set;}
        private static NumericUpDown Number()=>new NumericUpDown{Dock=DockStyle.Fill,Minimum=1,Maximum=100000,DecimalPlaces=2,ThousandsSeparator=true};

        public MaterialPlanDialog(NestingSnapshot snapshot,PlateNestGroup group,IReadOnlyList<SavedNestReceipt> receipts,
            int preferredIndex,string projectReference,AbeNestDocument? abe=null,bool automaticRequisition=true,Action<MaterialPlan>? validatePlan=null)
        {
            this.snapshot=snapshot;this.group=group;this.abe=abe;
            this.validatePlan=validatePlan;
            suggestedRequisition=projectReference.Trim();
            this.automaticRequisition=automaticRequisition;
            Text=abe==null?"Material for nesting":"Sheet for imported ABE nest";Font=HanlonTheme.BodyFont();StartPosition=FormStartPosition.CenterParent;
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
            var stocks=LoadedNestResult.InventoryNodes(snapshot.RequestRoot).ToList();
            var options=PlateSelection.StockOptions(snapshot,group,receipts).OrderBy(o=>o.InventoryIndex==preferredIndex?0:StockGeometry.IsRemnant(stocks[o.InventoryIndex])?1:2)
                .ThenBy(o=>StockGeometry.Mm(stocks[o.InventoryIndex],"Length")*StockGeometry.Mm(stocks[o.InventoryIndex],"Width")).ToList();
            int stockHeight=options.Count==0?0:Math.Clamp(options.Count*24+8,72,220);
            ClientSize=abe==null?new Size(900,580):new Size(720,188+(options.Count==0?0:stockHeight+32));
            MinimumSize=abe==null?new Size(740,540):new Size(580,options.Count==0?220:290);
            MinimizeBox=false;MaximizeBox=false;ShowInTaskbar=false;
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(16),Margin=Padding.Empty,ColumnCount=1,RowCount=5};Controls.Add(layout);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach(var row in new[]{new RowStyle(SizeType.Absolute,40),new RowStyle(SizeType.Percent,100),new RowStyle(SizeType.Absolute,35),new RowStyle(SizeType.Absolute,144),new RowStyle(SizeType.Absolute,44)})layout.RowStyles.Add(row);
            if(abe!=null)
            {
                layout.RowStyles[0].Height=32;
                if(options.Count==0)layout.RowStyles[1]=new RowStyle(SizeType.Absolute,0);
                layout.RowStyles[2].Height=options.Count==0?0:32;
                layout.RowStyles[3].Height=72;layout.RowStyles[4].Height=52;
                purchase.Text="Use one new sheet requirement";
                stockList.Visible=purchase.Visible=options.Count>0;
            }
            layout.Controls.Add(new Label{Dock=DockStyle.Fill,Margin=Padding.Empty,Text=abe==null?group.Label:abe.Reference.ProgramName,
                AutoEllipsis=true,Font=new Font(Font,FontStyle.Bold),TextAlign=ContentAlignment.MiddleLeft,UseMnemonic=false},0,0);
            stockList.Margin=Padding.Empty;
            layout.Controls.Add(stockList,0,1);
            layout.Controls.Add(purchase,0,2);
            var fields=new TableLayoutPanel{Dock=DockStyle.Fill,Margin=Padding.Empty,Padding=Padding.Empty,ColumnCount=5,RowCount=abe==null?3:2};
            if(abe==null)
            {
                fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,132));fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
                fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,16));
                fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,132));fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
                for(int i=0;i<3;i++)fields.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
            }
            else
            {
                fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100F/3));fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,12));
                fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100F/3));fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,12));
                fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100F/3));
                fields.RowStyles.Add(new RowStyle(SizeType.Absolute,24));fields.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
            }
            void Label(string text,int c,int r)=>fields.Controls.Add(new Label{Text=text,Dock=DockStyle.Fill,Margin=Padding.Empty,Padding=new Padding(0,0,8,0),TextAlign=ContentAlignment.MiddleLeft,UseMnemonic=false},c,r);
            void Field(Control control,int c,int r,int span=1)
            {
                // Native single-line controls keep their preferred height. With
                // horizontal anchors only, TableLayoutPanel centers each control
                // on the same row midpoint used by its MiddleLeft label.
                control.Dock=DockStyle.None;control.Anchor=AnchorStyles.Left|AnchorStyles.Right;control.Margin=Padding.Empty;
                fields.Controls.Add(control,c,r);if(span>1)fields.SetColumnSpan(control,span);
            }
            if(abe==null)
            {
                Label("New length (mm)",0,0);Field(length,1,0);Label("Width (mm)",3,0);Field(width,4,0);
                Label("Requisition #",0,1);Field(requisition,1,1,4);
                Label("Purchase via",0,2);Field(destination,1,2);Label("Existing PO #",3,2);Field(po,4,2);
            }
            else
            {
                Label("Requisition #",0,0);Field(requisition,0,1);
                Label("Length (mm)",2,0);Field(length,2,1);
                Label("Width (mm)",4,0);Field(width,4,1);
            }
            destination.Items.AddRange(new object[]{"Project requisition","Existing purchase order"});destination.SelectedIndex=0;
            destination.SelectedIndexChanged+=(_,_)=>po.Enabled=purchase.Checked&&destination.SelectedIndex==1;
            purchase.CheckedChanged+=(_,_)=>{length.Enabled=width.Enabled=destination.Enabled=purchase.Checked;po.Enabled=purchase.Checked&&destination.SelectedIndex==1;
                if(abe!=null&&purchase.Checked&&!changingStock){changingStock=true;for(int i=0;i<stockList.Items.Count;i++)stockList.SetItemChecked(i,false);changingStock=false;}};
            requisition.Text=projectReference;layout.Controls.Add(fields,0,3);
            var actions=new TableLayoutPanel{Dock=DockStyle.Fill,Margin=Padding.Empty,Padding=new Padding(0,8,0,0),ColumnCount=4,RowCount=1};
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,112));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,8));actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,112));
            actions.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var next=new HanlonButton{Text="Select",Primary=true,Dock=DockStyle.Fill,Margin=Padding.Empty,UseVisualStyleBackColor=false,FlatStyle=FlatStyle.Flat,
                BackColor=HanlonTheme.Brand,ForeColor=Color.White};
            next.FlatAppearance.BorderColor=HanlonTheme.Brand;
            var cancel=new HanlonButton{Text="Cancel",Dock=DockStyle.Fill,Margin=Padding.Empty,DialogResult=DialogResult.Cancel,
                UseVisualStyleBackColor=false,FlatStyle=FlatStyle.Flat,BackColor=Color.White};
            cancel.FlatAppearance.BorderColor=HanlonTheme.InputLine;
            actions.Controls.Add(cancel,1,0);actions.Controls.Add(next,3,0);layout.Controls.Add(actions,0,4);AcceptButton=next;CancelButton=cancel;
            next.Click+=(_,_)=>AcceptPlan();
            stockList.DisplayMember=nameof(PlateStockOption.Label);
            foreach(var option in options)
            {
                var material=LoadedNestResult.CreateTemplate(snapshot,option.InventoryIndex).Material;
                stockList.Items.Add(option,abe==null&&(!material.RequiresOutline||material.StockContour.Count>0));
            }
            if(abe!=null)stockList.ItemCheck+=(_,e)=>
            {
                if(changingStock||e.NewValue!=CheckState.Checked)return;
                changingStock=true;purchase.Checked=false;
                for(int i=0;i<stockList.Items.Count;i++)if(i!=e.Index)stockList.SetItemChecked(i,false);
                changingStock=false;
            };
            if(options.Count==0){stockList.Items.Add("No matching available stock. Enter the new sheet size below.",false);stockList.Enabled=false;}
            var suggestion=snapshot.OriginalStock.FirstOrDefault(s=>NestingSnapshot.Field(s,"Shape").Equals(group.Shape,StringComparison.OrdinalIgnoreCase)
                &&NestingSnapshot.Field(s,"Grade").Equals(group.Grade,StringComparison.OrdinalIgnoreCase)&&Math.Abs(StockGeometry.Mm(s,"PlateThickness","Thickness")-group.ThicknessMm)<0.01m);
            length.Value=Math.Clamp(suggestion==null?3000:StockGeometry.Mm(suggestion,"Length"),length.Minimum,length.Maximum);
            width.Value=Math.Clamp(suggestion==null?1500:StockGeometry.Mm(suggestion,"Width"),width.Minimum,width.Maximum);
            HanlonTheme.Apply(this);
            stockList.BorderStyle=BorderStyle.FixedSingle;
        }
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if(abe==null)return;
            Rectangle work=Screen.FromControl(Owner??this).WorkingArea;
            int inset=(int)Math.Round(12*DeviceDpi/96.0);
            int maxWidth=Math.Max(1,work.Width-inset*2),maxHeight=Math.Max(1,work.Height-inset*2);
            MinimumSize=new Size(Math.Min(MinimumSize.Width,maxWidth),Math.Min(stockList.Enabled?MinimumSize.Height:Height,maxHeight));
            Size=new Size(Math.Min(Width,maxWidth),Math.Min(Height,maxHeight));
            Rectangle centre=Owner?.Bounds??work;
            Location=new Point(Math.Clamp(centre.Left+(centre.Width-Width)/2,work.Left,work.Right-Width),
                Math.Clamp(centre.Top+(centre.Height-Height)/2,work.Top,work.Bottom-Height));
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing)
                foreach(Control control in new Control[]{destination,po})if(control.Parent==null)control.Dispose();
            base.Dispose(disposing);
        }
        private void AcceptPlan()
        {
            try
            {
                string enteredRequisition=requisition.Text.Trim();
                if(enteredRequisition.Length==0)throw new InvalidOperationException("Enter a requisition number.");
                if(abe!=null&&!purchase.Checked)
                {
                    var checkedStock=stockList.CheckedItems.OfType<PlateStockOption>().ToList();
                    if(checkedStock.Count==1)
                    {
                        var selected=LoadedNestResult.CreateTemplate(snapshot,checkedStock[0].InventoryIndex).Material;
                        var proposed=AbeSheetSelection.SuggestNewRequirement(abe,selected,group);
                        if(proposed!=null)
                        {
                            if(MessageBox.Show(this,AbeSheetSelection.Prompt(selected,proposed),"Hanlon - Sheet selection",
                                MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
                            // The checkbox handler clears the selected stock.
                            // Build a fresh requirement below, retaining the
                            // requisition entered by the user and no stock IDs.
                            purchase.Checked=true;
                            length.Value=proposed.Length;width.Value=proposed.Width;
                            destination.SelectedIndex=0;po.Clear();
                        }
                    }
                }
                var plan=new MaterialPlan{Group=group,RequisitionNumber=enteredRequisition,
                    AutoRequisitionNumber=automaticRequisition&&enteredRequisition.Equals(suggestedRequisition,StringComparison.Ordinal)};
                plan.StockIndexes.AddRange(stockList.CheckedItems.OfType<PlateStockOption>().Select(o=>o.InventoryIndex));
                foreach(int index in plan.StockIndexes)StockGeometry.Validate(LoadedNestResult.CreateTemplate(snapshot,index).Material);
                if(purchase.Checked)
                {
                    if(destination.SelectedIndex==1&&po.Text.Trim().Length==0)throw new InvalidOperationException("Enter the existing purchase order number.");
                    plan.TargetPurchaseOrderNumber=destination.SelectedIndex==1?po.Text.Trim():"";
                    plan.NewSheet=new NestMaterial{Source="Purchase",Shape=group.Shape,Grade=group.Grade,Thickness=group.ThicknessMm,
                        Length=length.Value,Width=width.Value,Unit="MM",Metric=true,
                        Dimensions=group.ThicknessMm.ToString("0.########",CultureInfo.InvariantCulture)+"X"+width.Value.ToString("0.########",CultureInfo.InvariantCulture)};
                }
                if(plan.StockIndexes.Count==0&&plan.NewSheet==null)throw new InvalidOperationException("Choose available stock or enable new sheets for the shortage.");
                if(abe!=null)
                {
                    if(plan.StockIndexes.Count+(plan.NewSheet==null?0:1)!=1)throw new InvalidOperationException("An NC/SY2 pair describes one sheet. Choose one stock row or one new sheet requirement.");
                    // Every ABE code uses the material of its matched PowerFab
                    // parts, as requested; no material-code confirmation prompt.
                    abe.Reference.MaterialConfirmed=true;
                }
                // Verify the chosen stock before closing. A layout that does
                // not fit can use another sheet without being skipped.
                validatePlan?.Invoke(plan);
                Plan=plan;DialogResult=DialogResult.OK;Close();
            }
            catch(Exception ex){MessageBox.Show(this,ex.Message,"Hanlon - Material",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        }
    }
}
