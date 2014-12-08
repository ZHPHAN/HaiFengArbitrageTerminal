using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using ComponentFactory.Krypton.Toolkit;
using Quote2015;
using Trade2015;
using System.Diagnostics;

//挂价委托,一腿成交后,另一腿撤单后市价追单
//增加市价委托功能(2014.9.23)
//增加发单前交易所状态过滤(2014.9.25)
//行情处理做了调整(2014.9.25)
//增加平仓功能,右键菜单(挂/市)(2014.9.26)
//增加改价功能(2014.9.26)
//修正:一腿成交量不更新的bug(2014.9.29)
//修正:价差显示未计算价差比(2014.9.29)
//成交后声音提醒(2014.10.9)
//增加时间段过滤:不触发,有挂单撤掉(2014.10.9)
//增加按钮"启动","暂停",默认为暂停.(2014.10.9)
//---------


namespace ArbitrageTerminal
{
	public partial class Arbitrage : UserControl
	{
		#region 配置

		readonly string _file = Environment.CurrentDirectory + "\\ArbitrageTerminal.config";
		private readonly ConcurrentDictionary<string, string> _config = new ConcurrentDictionary<string, string>();
		private string GetConfig(string pKey)
		{
			string rtn;
			_config.TryGetValue(pKey, out rtn);
			return rtn;//_config.AppSettings.Settings[pKey] == null ? string.Empty : _config.AppSettings.Settings[pKey].Value;
		}
		private void SetConfig(string pKey, string pValue)
		{
			_config.AddOrUpdate(pKey, pValue, (k, v) => pValue);
		}
		#endregion

		public Arbitrage(Trade pTrade, Quote pQuote)
		{
			_t = pTrade;
			_q = pQuote;
			this.Load += UserControl_Load;
			InitializeComponent();
		}

		private readonly Timer _timer = new Timer
		{
			Interval = 1000,
		};

		private void UserControl_Load(object sender, EventArgs e)
		{
			if (_q != null)
				_q.OnRtnTick += _q_OnRtnTick;  //行情触发

			_t.OnRtnCancel += _t_OnRtnCancel;
			_t.OnRtnError += _t_OnRtnError;
			_t.OnRtnOrder += _t_OnRtnOrder;
			_t.OnRtnTrade += _t_OnRtnTrade;
			this.kryptonComboBoxLeg1.Items.AddRange(_t.DicInstrumentField.Keys.ToArray());
			this.kryptonComboBoxLeg2.Items.AddRange(_t.DicInstrumentField.Keys.ToArray());

			this.kryptonButtonHang.Click += Order;
			this.kryptonDataGridView1.CellClick += kryptonDataGridView1_CellClick;
			this.kryptonComboBoxLeg1.SelectedValueChanged += kryptonComboBoxLeg_SelectedValueChanged;
			this.kryptonComboBoxLeg2.SelectedValueChanged += kryptonComboBoxLeg_SelectedValueChanged;

			//设置表格
			foreach (FieldInfo v in typeof(Stra).GetFields())
			{
				_dt.Columns.Add(v.Name, v.FieldType);
			}

			_dt.PrimaryKey = new[] { _dt.Columns["StraID"] };
			this.kryptonDataGridView1.DataSource = _dt;

			string[] names = { "Direction", "Instrument1", "Instrument2", "IsMarket", "Offset", "Price", "PriceTraded", "Rate1", "Rate2", "Status", "StraID", "Volume1", "Volume2", "VolumeTraded1", "VolumeTraded2" };
			string[] txts = { "买卖", "合约1", "合约2", "市价", "开平", "触发价", "成交价", "价差1", "价差2", "状态", "标识", "数量1", "数据2", "成交量1", "成交量2" };

			//修改表头
			foreach (FieldInfo v in typeof(Stra).GetFields())
			{
				//允许编辑价格
				if (v.Name == "Price")
				{
					this.kryptonDataGridView1.Columns[v.Name].DefaultCellStyle.BackColor = Color.LightGray;
					this.kryptonDataGridView1.Columns[v.Name].DefaultCellStyle.ForeColor = Color.HotPink;
				}
				else
					this.kryptonDataGridView1.Columns[v.Name].ReadOnly = true;

				int idx = names.ToList().IndexOf(v.Name);
				if (idx >= 0)
					this.kryptonDataGridView1.Columns[v.Name].HeaderText = txts[idx];

				var col = this.kryptonDataGridView1.Columns[v.Name];
				//格式化
				if (v.FieldType == typeof(double))
				{
					col.DefaultCellStyle.Format = "N2";
					col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
				}
				else if (v.FieldType == typeof(int))
				{
					col.DefaultCellStyle.Format = "N0";
					col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
				}
				else if (v.FieldType.IsEnum)
				{
					col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
				}
				else if (v.Name == "ExchangeID")
					col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
			}

			this.kryptonDataGridView1.Columns.Add(new DataGridViewButtonColumn
			{
				Name = "remove",
				HeaderText = "操作",
				Text = "删除",
				UseColumnTextForButtonValue = true,
			});
			kryptonDataGridView1.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.ColumnHeader);
			this.kryptonDataGridView1.Columns.Add(new DataGridViewButtonColumn
			{
				Name = "start",
				HeaderText = "启动",
				//Text = "暂停",
				UseColumnTextForButtonValue = false,
				Width = 80,
				DefaultCellStyle = new DataGridViewCellStyle { NullValue = "暂停" }
			});

			//右键平仓
			foreach (KryptonContextMenuItems v in this.kryptonContextMenu1.Items)
			{
				foreach (KryptonContextMenuItem item in v.Items)
				{
					item.Click += item_Click;
				}
			}

			if (File.Exists(_file))
			{
				foreach (string line in File.ReadAllLines(_file))
				{
					if (string.IsNullOrEmpty(line)) continue;
					_config.TryAdd(line.Split(',')[0], line.Split(',')[1]);
				}
			}

			if (!string.IsNullOrEmpty(GetConfig("_noTouch1")))
				_noTouch1 = bool.Parse(GetConfig("_noTouch1"));
			if (!string.IsNullOrEmpty(GetConfig("_noTouch2")))
				_noTouch1 = bool.Parse(GetConfig("_noTouch2"));

			if (!string.IsNullOrEmpty(GetConfig("_noTouchT1")))
				this.kryptonDateTimePickerNoTouch1.Value = DateTime.Today.Add(TimeSpan.Parse(GetConfig("_noTouchT1")));
			if (!string.IsNullOrEmpty(GetConfig("_noTouchT1_2")))
				this.kryptonDateTimePickerNoTouch1_2.Value = DateTime.Today.Add(TimeSpan.Parse(GetConfig("_noTouchT1_2")));
			if (!string.IsNullOrEmpty(GetConfig("_noTouchT2")))
				this.kryptonDateTimePickerNoTouch2.Value = DateTime.Today.Add(TimeSpan.Parse(GetConfig("_noTouchT2")));
			if (!string.IsNullOrEmpty(GetConfig("_noTouchT2_2")))
				this.kryptonDateTimePickerNoTouch2_2.Value = DateTime.Today.Add(TimeSpan.Parse(GetConfig("_noTouchT2_2")));
			_noTouchT1 = this.kryptonDateTimePickerNoTouch1.Value.TimeOfDay;
			_noTouchT1_2 = this.kryptonDateTimePickerNoTouch1_2.Value.TimeOfDay;
			_noTouchT2 = this.kryptonDateTimePickerNoTouch2.Value.TimeOfDay;
			_noTouchT2_2 = this.kryptonDateTimePickerNoTouch2_2.Value.TimeOfDay;

			_timer.Tick += _timer_Tick;
			_timer.Start();
		}

		void item_Click(object sender, EventArgs e)
		{
			DataGridViewRow row = (DataGridViewRow)this.kryptonContextMenu1.Caller;
			string id = (string)row.Cells["StraID"].Value;
			Stra stra;
			if (_dicStra.TryGetValue(id, out stra))
			{
				InstrumentField instField1;
				InstrumentField instField2;
				if (!(_t.DicInstrumentField.TryGetValue(stra.Instrument1, out instField1) && _t.DicInstrumentField.TryGetValue(stra.Instrument2, out instField2)))
					return;

				MarketData t1;
				MarketData t2;
				if (!(_q.DicTick.TryGetValue(stra.Instrument1, out t1) && _q.DicTick.TryGetValue(stra.Instrument2, out t2)))
					return;

				id = NewStra(stra.Instrument1, stra.Instrument2, stra.Direction == DirectionType.Buy ? DirectionType.Sell : DirectionType.Buy,
					OffsetType.Close, stra.Direction == DirectionType.Buy ? t1.AskPrice - t2.BidPrice : t1.BidPrice - t2.AskPrice,
					stra.Rate1, stra.Rate2, stra.VolumeTraded1, stra.VolumeTraded2, ((KryptonContextMenuItem)sender).Text == "市价平仓");

				if (_dicStra.TryGetValue(id, out stra))
				{
					stra.Status = ArbStatus.Normal;
					//发单
					if (stra.Direction == DirectionType.Buy)
					{
						if (stra.IsMarket)// && ask <= stra.Price)
						{
							if (instField1.ExchangeID == "SHFE")
								_t.ReqOrderInsert(stra.Instrument1, DirectionType.Buy, stra.Offset, t1.UpperLimitPrice, stra.Volume1, pCustom: stra.StraID);
							else
								_t.ReqOrderInsert(stra.Instrument1, DirectionType.Buy, stra.Offset, t1.AskPrice, stra.Volume1, pType: OrderType.Market, pCustom: stra.StraID);

							if (instField2.ExchangeID == "SHFE")
								_t.ReqOrderInsert(stra.Instrument2, DirectionType.Sell, stra.Offset, t2.LowerLimitPrice, stra.Volume2, pCustom: stra.StraID);
							else
								_t.ReqOrderInsert(stra.Instrument2, DirectionType.Sell, stra.Offset, t2.BidPrice, stra.Volume2, pType: OrderType.Market, pCustom: stra.StraID);
						}
						else //挂价
						{
							_t.ReqOrderInsert(stra.Instrument1, DirectionType.Buy, stra.Offset, t1.BidPrice, stra.Volume1, pCustom: stra.StraID);
							_t.ReqOrderInsert(stra.Instrument2, DirectionType.Sell, stra.Offset, t2.AskPrice, stra.Volume2, pCustom: stra.StraID);
						}
					}
					else if (stra.Direction == DirectionType.Sell)
					{
						if (stra.IsMarket)// && bid >= stra.Price)
						{
							if (instField1.ExchangeID == "SHFE")
								_t.ReqOrderInsert(stra.Instrument1, DirectionType.Sell, stra.Offset, t1.LowerLimitPrice, stra.Volume1, pCustom: stra.StraID);
							else
								_t.ReqOrderInsert(stra.Instrument1, DirectionType.Sell, stra.Offset, t1.BidPrice, stra.Volume1, pType: OrderType.Market, pCustom: stra.StraID);

							if (instField2.ExchangeID == "SHFE")
								_t.ReqOrderInsert(stra.Instrument2, DirectionType.Buy, stra.Offset, t2.UpperLimitPrice, stra.Volume2, pCustom: stra.StraID);
							else
								_t.ReqOrderInsert(stra.Instrument2, DirectionType.Buy, stra.Offset, t2.AskPrice, stra.Volume2, pType: OrderType.Market, pCustom: stra.StraID);
						}
						else
						{
							_t.ReqOrderInsert(stra.Instrument1, DirectionType.Sell, stra.Offset, t1.AskPrice, stra.Volume1, pCustom: stra.StraID);
							_t.ReqOrderInsert(stra.Instrument2, DirectionType.Buy, stra.Offset, t2.BidPrice, stra.Volume2, pCustom: stra.StraID);
						}
					}
				}
			}
		}

		protected override void OnHandleDestroyed(EventArgs e)
		{
			_timer.Stop();
			string txt = _dicStra.Aggregate(string.Empty, (current1, vi) => vi.GetType().GetFields().Aggregate(current1, (current, fi) => current + (fi.GetValue(vi) + ",")).TrimEnd(',') + "\r\n");
			File.WriteAllText(this.GetType().Name + ".txt", txt);

			File.WriteAllText(_file, _config.Aggregate(txt, (current, v) => current + (v.Key + "," + v.Value + "\r\n")));

			if (_q != null)
				_q.OnRtnTick -= _q_OnRtnTick;  //行情触发

			_t.OnRtnCancel -= _t_OnRtnCancel;
			_t.OnRtnError -= _t_OnRtnError;
			_t.OnRtnOrder -= _t_OnRtnOrder;
			_t.OnRtnTrade -= _t_OnRtnTrade;
			base.OnHandleDestroyed(e);
		}

		private readonly ConcurrentDictionary<string, Stra> _dicStra = new ConcurrentDictionary<string, Stra>();
		private readonly ConcurrentDictionary<Stra, List<int>> _straOrdersId = new ConcurrentDictionary<Stra, List<int>>();
		private readonly Trade _t;
		private readonly Quote _q;
		private readonly DataTable _dt = new DataTable();
		private readonly List<int> _reSend = new List<int>();
		private int _maxStraID;
		private readonly ConcurrentQueue<Tuple<Stra, string>> _queueModifiedStra = new ConcurrentQueue<Tuple<Stra, string>>();
		private bool _noTouch1 = true, _noTouch2 = true;
		private TimeSpan _noTouchT1, _noTouchT1_2, _noTouchT2, _noTouchT2_2;
		private TimeSpan _time;
		private readonly Stopwatch _watch = new Stopwatch();
		private readonly List<string> _listStarted = new List<string>();

		//添加
		private void Order(object sender, EventArgs e)
		{
			NewStra(this.kryptonComboBoxLeg1.Text, this.kryptonComboBoxLeg2.Text, this.kryptonRadioButtonBuy.Checked ? DirectionType.Buy : DirectionType.Sell,
				this.kryptonRadioButtonOpen.Checked ? OffsetType.Open : OffsetType.Close, (double)this.kryptonNumericUpDownPrice.Value, (double)this.kryptonNumericUpDownRate1.Value,
				(double)this.kryptonNumericUpDownRate2.Value, (int)this.kryptonNumericUpDownVol1.Value, (int)this.kryptonNumericUpDownVol2.Value, sender == this.kryptonButtonMarket);
		}

		private string NewStra(string pInst1, string pInst2, DirectionType pDire, OffsetType pOffset, double pPrice, double pRate1, double pRate2, int pVol1, int pVol2, bool pIsMarket)
		{
			Stra stra = new Stra
					{
						StraID = _maxStraID++.ToString(CultureInfo.InvariantCulture),
						Instrument1 = pInst1,
						Instrument2 = pInst2,
						Direction = pDire,
						Offset = pOffset,
						Price = pPrice,
						Rate1 = pRate1,
						Rate2 = pRate2,
						Volume1 = pVol1,
						Volume2 = pVol2,
						Status = ArbStatus.NotTouch,
						IsMarket = pIsMarket,
					};
			if (_dicStra.TryAdd(stra.StraID, stra))
			{
				DataRow dr = _dt.NewRow();
				foreach (FieldInfo fi in typeof(Stra).GetFields())
					dr[fi.Name] = fi.GetValue(stra);
				_dt.Rows.Add(dr);

				string txt = _dicStra.Aggregate(string.Empty, (current1, vi) => vi.GetType().GetFields().Aggregate(current1, (current, fi) => current + (fi.GetValue(vi) + ",")).TrimEnd(',') + "\r\n");
				File.WriteAllText(this.GetType().Name + ".txt", txt);
			}
			return stra.StraID;
		}

		//删除 逻辑
		private void kryptonDataGridView1_CellClick(object sender, DataGridViewCellEventArgs e)
		{
			if (e.RowIndex < 0 || e.ColumnIndex < 0)
			{
				return;
			}

			string straId = (string)this.kryptonDataGridView1["StraID", e.RowIndex].Value;
			if (this.kryptonDataGridView1.Columns[e.ColumnIndex].Name == "remove")
			{
				Stra stra;
				if (_dicStra.TryRemove(straId, out stra))
				{
					List<int> ls;
					if (_straOrdersId.TryRemove(stra, out ls))
						foreach (var id in ls)
						{
							if (_t.DicOrderField[id].Status != OrderStatus.Filled && _t.DicOrderField[id].Status != OrderStatus.Canceled)
							{
								_t.ReqOrderAction(id);
							}
						}
					_dt.Rows.Remove(_dt.Rows.Find(straId));
				}
			}
			else if (this.kryptonDataGridView1.Columns[e.ColumnIndex].Name == "start")
			{
				DataGridViewButtonCell cell = (DataGridViewButtonCell)this.kryptonDataGridView1[e.ColumnIndex, e.RowIndex];
				if (cell.FormattedValue.Equals("已启动"))
				{
					_listStarted.Remove(straId);
					cell.Value = "暂停";
				}
				else
				{
					cell.Value = "已启动";
					_listStarted.Add(straId);
				}
			}
		}

		private void _timer_Tick(object sender, EventArgs e)
		{
			if (_t.DicInstrumentField.Count == 0)
				return;	//合约查询后再做后续处理
			Tuple<Stra, string> sf;
			while (_queueModifiedStra.TryDequeue(out sf))
			{
				DataRow dr = _dt.Rows.Find(sf.Item1.StraID);
				if (dr == null)
					continue;
				FieldInfo fi = typeof(Stra).GetField(sf.Item2);
				dr[fi.Name] = fi.GetValue(sf.Item1);
			}

			if (_t == null || _t.DicInstrumentField.Count == 0)
				return;
			if (_q != null && _q.IsLogin)
			{
				MarketData t1;
				MarketData t2;
				if (_q.DicTick.TryGetValue(this.kryptonComboBoxLeg1.Text, out t1) && _q.DicTick.TryGetValue(this.kryptonComboBoxLeg2.Text, out t2))
				{
					this.kryptonLabelAsk.Text = (t1.AskPrice * (double)this.kryptonNumericUpDownRate1.Value - t2.BidPrice * (double)this.kryptonNumericUpDownRate2.Value).ToString("N2");
					this.kryptonLabelBid.Text = (t1.BidPrice * (double)this.kryptonNumericUpDownRate1.Value - t2.AskPrice * (double)this.kryptonNumericUpDownRate2.Value).ToString("N2");
				}
			}

			//撤单非触发时间段内的挂段
			TimeSpan now = _time.Add(_watch.Elapsed);
			//时间过滤
			if ((_noTouch1 && now >= _noTouchT1 && now <= _noTouchT1_2) || (_noTouch2 && now >= _noTouchT2 && now <= _noTouchT2_2))
			{
				foreach (var v in _straOrdersId)
				{
					//所有委托均为normal状态
					if (_t.DicOrderField.Where(n => v.Value.IndexOf(n.Key) >= 0).Count(n => n.Value.Status != OrderStatus.Normal) == 0)
					{
						//全部撤掉
						foreach (var oid in v.Value)
						{
							_t.ReqOrderAction(oid);
						}
					}
				}
			}
		}

		// 行情触发
		void _q_OnRtnTick(object sender, TickEventArgs e)
		{
			if (_time == new TimeSpan(0, 0, 0))
			{
				_time = TimeSpan.Parse(e.Tick.UpdateTime);
				_watch.Restart();
			}

			if (!_timer.Enabled)
			{
				_q.OnRtnTick -= _q_OnRtnTick;
				return;
			}

			TimeSpan now = _time.Add(_watch.Elapsed);
			//时间过滤
			if (_noTouch1 && now >= _noTouchT1 && now <= _noTouchT1_2)
				return;
			if (_noTouch2 && now >= _noTouchT2 && now <= _noTouchT2_2)
				return;

			foreach (Stra stra in _dicStra.Values)
			{
				if (stra.Instrument1 != e.Tick.InstrumentID && stra.Instrument2 != e.Tick.InstrumentID)
					continue;
				if (stra.Status != ArbStatus.NotTouch)
					continue;

				InstrumentField instField1, instField2;
				if (!_t.DicInstrumentField.TryGetValue(stra.Instrument1, out instField1))
					continue;
				if (!_t.DicInstrumentField.TryGetValue(stra.Instrument2, out instField2))
					continue;
				//交易时段过滤
				ExchangeStatusType excStatus;
				if (!_t.DicExcStatus.TryGetValue(instField1.ProductID, out excStatus) || excStatus != ExchangeStatusType.Trading)
					continue;
				if (!_t.DicExcStatus.TryGetValue(instField2.ProductID, out excStatus) || excStatus != ExchangeStatusType.Trading)
					continue;

				MarketData t1, t2;
				if (!_q.DicTick.TryGetValue(stra.Instrument1, out t1))
					continue;
				if (!_q.DicTick.TryGetValue(stra.Instrument2, out t2))
					continue;

				double ask = t1.AskPrice * stra.Rate1 - t2.BidPrice * stra.Rate2;
				double bid = t1.BidPrice * stra.Rate1 - t2.AskPrice * stra.Rate2;
				if (stra.Status != ArbStatus.NotTouch)
					continue;	//防止重复发单(两个合约数据同时到达)
				//是否启动过滤
				if (_listStarted.IndexOf(stra.StraID) < 0)
					continue;

				if (stra.Direction == DirectionType.Buy)
				{
					//if (bid <= stra.Price)
					if ((stra.IsMarket ? ask : bid) <= stra.Price)
					{
						stra.Status = ArbStatus.Normal;
						_queueModifiedStra.Enqueue(new Tuple<Stra, string>(stra, "Status")); //用于刷新
						if (stra.IsMarket)// && ask <= stra.Price)
						{
							if (instField1.ExchangeID == "SHFE")
								_t.ReqOrderInsert(stra.Instrument1, DirectionType.Buy, stra.Offset, t1.UpperLimitPrice, stra.Volume1, pCustom: stra.StraID);
							else
								_t.ReqOrderInsert(stra.Instrument1, DirectionType.Buy, stra.Offset, t1.AskPrice, stra.Volume1, pType: OrderType.Market, pCustom: stra.StraID);

							if (instField2.ExchangeID == "SHFE")
								_t.ReqOrderInsert(stra.Instrument2, DirectionType.Sell, stra.Offset, t2.LowerLimitPrice, stra.Volume2, pCustom: stra.StraID);
							else
								_t.ReqOrderInsert(stra.Instrument2, DirectionType.Sell, stra.Offset, t2.BidPrice, stra.Volume2, pType: OrderType.Market, pCustom: stra.StraID);
						}
						else //挂价
						{
							_t.ReqOrderInsert(stra.Instrument1, DirectionType.Buy, stra.Offset, t1.BidPrice, stra.Volume1, pCustom: stra.StraID);
							_t.ReqOrderInsert(stra.Instrument2, DirectionType.Sell, stra.Offset, t2.AskPrice, stra.Volume2, pCustom: stra.StraID);
						}
					}
				}
				else if (stra.Direction == DirectionType.Sell)
				{
					//if (ask >= stra.Price)
					if ((stra.IsMarket ? bid : ask) >= stra.Price)
					{
						stra.Status = ArbStatus.Normal;
						_queueModifiedStra.Enqueue(new Tuple<Stra, string>(stra, "Status")); //用于刷新

						if (stra.IsMarket)// && bid >= stra.Price)
						{
							if (instField1.ExchangeID == "SHFE")
								_t.ReqOrderInsert(stra.Instrument1, DirectionType.Sell, stra.Offset, t1.LowerLimitPrice, stra.Volume1, pCustom: stra.StraID);
							else
								_t.ReqOrderInsert(stra.Instrument1, DirectionType.Sell, stra.Offset, t1.BidPrice, stra.Volume1, pType: OrderType.Market, pCustom: stra.StraID);

							if (instField2.ExchangeID == "SHFE")
								_t.ReqOrderInsert(stra.Instrument2, DirectionType.Buy, stra.Offset, t2.UpperLimitPrice, stra.Volume2, pCustom: stra.StraID);
							else
								_t.ReqOrderInsert(stra.Instrument2, DirectionType.Buy, stra.Offset, t2.AskPrice, stra.Volume2, pType: OrderType.Market, pCustom: stra.StraID);
						}
						else
						{
							_t.ReqOrderInsert(stra.Instrument1, DirectionType.Sell, stra.Offset, t1.AskPrice, stra.Volume1, pCustom: stra.StraID);
							_t.ReqOrderInsert(stra.Instrument2, DirectionType.Buy, stra.Offset, t2.BidPrice, stra.Volume2, pCustom: stra.StraID);
						}
					}
				}
			}
		}

		void _t_OnRtnTrade(object sender, TradeArgs e)
		{
			var trade = (Trade)sender;
			var field = e.Value;

			var vap = _straOrdersId.FirstOrDefault(n => n.Value.IndexOf(e.Value.OrderID) >= 0);
			if (vap.Key == null)
				return;
			var ids = vap.Value;
			var o1 = _t.DicOrderField.Values.Where(n => ids.IndexOf(n.OrderID) >= 0 && n.InstrumentID == vap.Key.Instrument1);
			vap.Key.VolumeTraded1 = o1.Sum(n => n.Volume - n.VolumeLeft);

			var p1 = o1.Sum(n => n.AvgPrice * (n.Volume - n.VolumeLeft)) / vap.Key.VolumeTraded1;
			var o2 = _t.DicOrderField.Values.Where(n => ids.IndexOf(n.OrderID) >= 0 && n.InstrumentID == vap.Key.Instrument2);
			vap.Key.VolumeTraded2 = o2.Sum(n => n.Volume - n.VolumeLeft);

			if (vap.Key.Instrument1 == field.InstrumentID)
				_queueModifiedStra.Enqueue(new Tuple<Stra, string>(vap.Key, "VolumeTraded1")); //用于刷新
			else if (vap.Key.Instrument2 == field.InstrumentID)
				_queueModifiedStra.Enqueue(new Tuple<Stra, string>(vap.Key, "VolumeTraded2")); //用于刷新

			var p2 = o2.Sum(n => n.AvgPrice * (n.Volume - n.VolumeLeft)) / vap.Key.VolumeTraded2;
			double p = p1 - p2;

			if (!double.IsNaN(p))
			{
				vap.Key.PriceTraded = p;
				if (vap.Key.VolumeTraded1 == vap.Key.Volume1 && vap.Key.Volume2 == vap.Key.VolumeTraded2)
					vap.Key.Status = ArbStatus.Filled;
				else
					vap.Key.Status = ArbStatus.Partial;
				_queueModifiedStra.Enqueue(new Tuple<Stra, string>(vap.Key, "Status")); //用于刷新
				_queueModifiedStra.Enqueue(new Tuple<Stra, string>(vap.Key, "PriceTraded")); //用于刷新
			}
		}

		void _t_OnRtnOrder(object sender, OrderArgs e)
		{
			var trade = (Trade)sender;

			var vap = _dicStra.FirstOrDefault(n => n.Value.StraID == e.Value.Custom.Trim());
			if (vap.Value == null)
				return;
			var stra = vap.Value;
			var ls = _straOrdersId.GetOrAdd(stra, new List<int>());
			if (e.Value.IsLocal && e.Value.Status == OrderStatus.Normal)
			{
				//if (_curStra != null)
				{
					ls.Add(e.Value.OrderID);
				}
			}
			else if (!stra.IsMarket && e.Value.Status == OrderStatus.Filled)
			{
				//另一边未全部成交
				var instother = stra.Instrument2;
				var volother = stra.Volume2;
				if (e.Value.InstrumentID == stra.Instrument2)
				{
					instother = stra.Instrument1;
					volother = stra.Volume1;
				}
				var ofs = trade.DicOrderField.Where(n => ls.IndexOf(n.Key) >= 0 && n.Value.InstrumentID == instother);
				if (ofs.Sum(n => (n.Value.Volume - n.Value.VolumeLeft)) < volother)
				{
					foreach (var v in ofs)
					{
						if (v.Value.Status == OrderStatus.Canceled)
							continue;
						_reSend.Add(v.Key);
						//  成交后逻辑 // 
						trade.ReqOrderAction(v.Key);
					}
				}
			}
		}

		void _t_OnRtnCancel(object sender, OrderArgs e)
		{
			//重发委托
			var trade = (Trade)sender;
			if (_reSend.IndexOf(e.Value.OrderID) >= 0)
			{
				_reSend.Remove(e.Value.OrderID);
				InstrumentField instField;
				if (trade.DicInstrumentField.TryGetValue(e.Value.InstrumentID, out instField))
				{
					if (instField.ExchangeID == "SHFE")
					{
						double price = e.Value.Direction == DirectionType.Buy ? _q.DicTick[e.Value.InstrumentID].UpperLimitPrice : _q.DicTick[e.Value.InstrumentID].LowerLimitPrice;
						trade.ReqOrderInsert(e.Value.InstrumentID, e.Value.Direction, e.Value.Offset, price,
							e.Value.VolumeLeft, pType: OrderType.Limit, pCustom: e.Value.Custom);
					}
					else
						trade.ReqOrderInsert(e.Value.InstrumentID, e.Value.Direction, e.Value.Offset, 0,
							e.Value.VolumeLeft, pType: OrderType.Market, pCustom: e.Value.Custom);
				}
			}
		}

		void _t_OnRtnError(object sender, Trade2015.ErrorEventArgs e)
		{
			//_curStra = null;
			if (e.ErrorMsg.IndexOf("no sysid", StringComparison.Ordinal) >= 0)
			{
				//Thread.Sleep(200);
				//_t.ReqOrderAction(e.ErrorID);
			}
		}

		//选择合约:订阅
		void kryptonComboBoxLeg_SelectedValueChanged(object sender, EventArgs e)
		{
			if (_q != null)
			{
				_q.ReqSubscribeMarketData(((KryptonComboBox)sender).Text);
			}
		}

		//右键菜单
		private void kryptonDataGridView1_CellMouseClick(object sender, DataGridViewCellMouseEventArgs e)
		{
			if (e.RowIndex < 0 || e.ColumnIndex < 0)
				return;
			if (e.Button == MouseButtons.Right)
			{
				DataGridViewRow row = this.kryptonDataGridView1.Rows[e.RowIndex];
				if ((OffsetType)row.Cells["Offset"].Value == OffsetType.Open)
					this.kryptonContextMenu1.Show(row);
			}
		}

		//编辑
		private void kryptonDataGridView1_CellEndEdit(object sender, DataGridViewCellEventArgs e)
		{
			DataGridViewRow row = this.kryptonDataGridView1.Rows[e.RowIndex];
			Stra stra;
			if (_dicStra.TryGetValue((string)row.Cells["StraID"].Value, out stra))
			{
				FieldInfo fi = typeof(Stra).GetField(this.kryptonDataGridView1.Columns[e.ColumnIndex].Name);
				fi.SetValue(stra, this.kryptonDataGridView1[e.ColumnIndex, e.RowIndex].Value);

				_queueModifiedStra.Enqueue(new Tuple<Stra, string>(stra, fi.Name)); //用于刷新
			}
		}

		private void kryptonDataGridView1_CellBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
		{
			DataGridViewRow row = this.kryptonDataGridView1.Rows[e.RowIndex];
			if ((ArbStatus)row.Cells["Status"].Value != ArbStatus.NotTouch)
			{
				row.ReadOnly = true;
				e.Cancel = true;
			}
		}

		//格式化
		private void kryptonDataGridView1_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
		{
			if (e.RowIndex < 0 || e.ColumnIndex < 0)
			{
				return;
			}

			string[] keys = { "NotTouch", "Buy", "Sell", "Open", "Close", "CloseToday", "Speculation", "Arbitrage", "Hedge", "Normal", "Canceled", "Partial", "Filled" };
			string[] values = { "未触发", "  买", "卖  ", "开仓", "平仓", "平今", "投机", "套利", "套保", "委托", "已撤单", "部成", "全成" };

			DataGridViewCell cell = ((KryptonDataGridView)sender)[e.ColumnIndex, e.RowIndex];
			if (cell.ValueType.IsEnum)
			{
				string val = Enum.GetName(cell.ValueType, e.Value);
				int idx = keys.ToList().IndexOf(val);
				if (idx >= 0)
				{
					e.Value = values[idx];
					switch (values[idx])
					{
						case "  买":
							cell.Style.ForeColor = Color.Red;
							cell.Style.Alignment = DataGridViewContentAlignment.MiddleLeft;
							break;
						case "卖  ":
							cell.Style.ForeColor = Color.Green;
							cell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
							break;
					}
				}
			}
		}

		//限定时间范围
		private void kryptonDateTimePickerNoTouch1_ValueChanged(object sender, EventArgs e)
		{
			if (sender == this.kryptonDateTimePickerNoTouch1)
			{
				_noTouchT1 = this.kryptonDateTimePickerNoTouch1.Value.TimeOfDay;
				SetConfig("_noTouchT1", _noTouchT1.ToString());
			}
			else if (sender == this.kryptonDateTimePickerNoTouch1_2)
			{
				_noTouchT1_2 = this.kryptonDateTimePickerNoTouch1_2.Value.TimeOfDay;
				SetConfig("_noTouchT1_2", _noTouchT1_2.ToString());
			}
			else if (sender == this.kryptonDateTimePickerNoTouch2)
			{
				_noTouchT2 = this.kryptonDateTimePickerNoTouch2.Value.TimeOfDay;
				SetConfig("_noTouchT2", _noTouchT2.ToString());
			}
			else if (sender == this.kryptonDateTimePickerNoTouch2_2)
			{
				_noTouchT2_2 = this.kryptonDateTimePickerNoTouch2_2.Value.TimeOfDay;
				SetConfig("_noTouchT2_2", _noTouchT2_2.ToString());
			}
		}

		private void kryptonDateTimePickerNoTouch1_CheckedChanged(object sender, EventArgs e)
		{
			if (sender == this.kryptonDateTimePickerNoTouch1)
			{
				_noTouch1 = this.kryptonDateTimePickerNoTouch1.Checked;
				SetConfig("_noTouch1", _noTouch1.ToString());
			}
			else
			{
				_noTouch2 = this.kryptonDateTimePickerNoTouch2.Checked;
				SetConfig("_noTouch2", _noTouch2.ToString());
			}
		}
	}

	class Stra
	{
		public string StraID;
		public string Instrument1;
		public string Instrument2;
		public double Rate1;
		public double Rate2;
		public int Volume1;
		public int Volume2;
		public DirectionType Direction;
		public OffsetType Offset;
		public double Price;
		public double PriceTraded;
		public int VolumeTraded1;
		public int VolumeTraded2;
		public ArbStatus Status;
		/// <summary>
		/// 是否市价委托
		/// </summary>
		public bool IsMarket;
	}

	enum ArbStatus
	{
		NotTouch,
		Normal,
		Canceled,
		Partial,
		Filled,
	}
}
