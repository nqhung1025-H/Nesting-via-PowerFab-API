using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Hanlon.PowerFab.Nesting
{
    internal sealed class AbeBatchReviewDialog:Form
    {
        private readonly DataGridView grid=new DataGridView{Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false,
            RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill};
        private readonly PictureBox picture=new PictureBox{Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.White};
        private readonly Label details=new Label{Dock=DockStyle.Bottom,Height=64,AutoEllipsis=true,Padding=new Padding(8)};
        public List<AbeBatchItem> SelectedItems{get;private set;}=new List<AbeBatchItem>();
        public AbeBatchReviewDialog(AbeBatchRead batch,bool canLink,Func<AbeNestDocument,Image> render)
        {
            grid.DefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleCenter;
            grid.ColumnHeadersDefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleCenter;
            Text="Import ABE nests";Font=HanlonTheme.BodyFont();StartPosition=FormStartPosition.CenterParent;
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;ClientSize=new Size(1180,740);MinimumSize=new Size(940,600);
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(12)};Controls.Add(layout);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,55));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
            layout.Controls.Add(new Label{Dock=DockStyle.Fill,Text=batch.Items.Count(i=>i.Document!=null)+" sheet pairs | "+batch.Items.Count(i=>i.Document==null)+" input issues | "+batch.DuplicateSelections+" repeated selections ignored\nSelect sheets to link. Thickness and material are read from each pair.",AutoEllipsis=true},0,0);
            var split=new SplitContainer{Size=new Size(1140,600),Dock=DockStyle.Fill,SplitterDistance=570,Panel1MinSize=330,Panel2MinSize=330};
            split.Panel1.Controls.Add(grid);split.Panel2.Controls.Add(picture);split.Panel2.Controls.Add(details);layout.Controls.Add(split,0,1);
            grid.Columns.Add(new DataGridViewCheckBoxColumn{Name="Use",HeaderText="Use",FillWeight=30});
            foreach(string name in new[]{"Program","ABE → PF mm","Material","Job parts","Packers","Status"})grid.Columns.Add(new DataGridViewTextBoxColumn{Name=name,HeaderText=name,ReadOnly=true,FillWeight=name=="Program"?160:name=="Status"?170:65});
            foreach(var item in batch.Items)
            {
                var d=item.Document;string thickness=d==null?"":d.Thickness==d.PowerFabThickness?d.Thickness.ToString():d.Thickness+" → "+d.PowerFabThickness;
                int n=grid.Rows.Add(d!=null,item.Name,thickness,d?.Reference.Material??"",d?.ProductionCount.ToString()??"",d?.PackerCount.ToString()??"",item.Status);
                grid.Rows[n].Tag=item;grid.Rows[n].Cells[0].ReadOnly=d==null;grid.Rows[n].Cells[1].ToolTipText=item.NcPath+"\n"+item.Sy2Path;
            }
            void Preview()
            {
                picture.Image?.Dispose();picture.Image=null;
                if(grid.CurrentRow?.Tag is not AbeBatchItem item)return;
                details.Text=item.Status+"\n"+item.NcPath;
                if(item.Document==null)return;
                try{picture.Image=render(item.Document);}catch(Exception ex){details.Text="Preview: "+ex.Message;}
            }
            grid.SelectionChanged+=(_,_)=>Preview();Shown+=(_,_)=>Preview();
            var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Padding=new Padding(0,6,0,0)};
            var next=new HanlonButton{Text="Link selected sheets",Primary=true,Width=180,Height=34,Enabled=canLink&&batch.Items.Any(i=>i.Document!=null)};
            var close=new HanlonButton{Text="Close",Width=112,Height=34,DialogResult=DialogResult.Cancel};actions.Controls.Add(next);actions.Controls.Add(close);layout.Controls.Add(actions,0,2);
            next.Click+=(_,_)=>{grid.EndEdit();SelectedItems=grid.Rows.Cast<DataGridViewRow>().Where(r=>r.Cells[0].Value is true).Select(r=>r.Tag).OfType<AbeBatchItem>().Where(i=>i.Document!=null).ToList();
                if(SelectedItems.Count==0)return;DialogResult=DialogResult.OK;Close();};CancelButton=close;AcceptButton=next;
            HanlonTheme.Apply(this,picture);
        }
        protected override void Dispose(bool disposing){if(disposing){picture.Image?.Dispose();picture.Image=null;}base.Dispose(disposing);}
    }
}
