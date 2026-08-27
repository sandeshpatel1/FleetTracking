using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TrackingMVC.Models
{
    /// <summary>
    /// Maps to [atmparking].[dbo].[gps_trip_detail]
    /// </summary>
    [Table("gps_trip_detail", Schema = "dbo")]
    public class GpsTripDetail
    {
        [Key]
        [Column("pk")]
        public int Pk { get; set; }

        [Column("trip_ID")]
        public string? TripId { get; set; }

        [Column("vehicle_no")]
        [MaxLength(50)]
        public string? VehicleNo { get; set; }

        [Column("serial_no")]
        [MaxLength(50)]
        public string? SerialNo { get; set; }

        /// <summary>0 = Not Assigned, 1 = Assigned</summary>
        [Column("assiged_flag")]
        public int AssignedFlag { get; set; }

        [Column("driver_no")]
        [MaxLength(20)]
        public string? DriverNo { get; set; }

        /// <summary>FK to the assigned GPS device (IMEI)</summary>
        [Column("device_id")]
        [MaxLength(20)]
        public string? DeviceId { get; set; }
    }
}